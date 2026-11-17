using UdonSharp;
using UnityEngine;
using VRCKatamari.BallContact;
using VRCKatamari.BallMotor;
using VRCKatamari.Pickup;
using VRCKatamari.Story;

namespace VRCKatamari.Labs.PhysicsTestField
{
    /// <summary>
    /// One physics regression pass over the test field: switch cell, set the ball diameter, drive
    /// a scripted timeline, judge the cell's criteria, log one line per check.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the laboratory's only driver. It introduces no second physics and no second input
    /// path: registering the field is <c>LevelArenaHost.Register</c> (the sandbox, below), sizing is <see cref="LabBallSizeSetter"/>, driving is
    /// <c>motor.SetMoveAxes</c>, and every reading is a motor getter a shipped HUD would read too.
    /// <c>Docs/Design/BallMotor/PHYSICS_TEST_FIELD.md</c> is the design; this file is its section 7.
    /// </para>
    /// <para>
    /// <b>Switch order is not arbitrary.</b> Clear-then-register, then the kind whitelist, then the
    /// ball reset, then the prop reset, then the size. The reset must come after the arena (it
    /// re-derives the size and teleports the ball onto the new floor) and before the size (the reset
    /// re-prices the ball from zero volume, so sizing first would throw the work away). The radius
    /// used for the spawn height is read AFTER the reset, from the ball the reset just restored;
    /// reading it before the reset used the previous run's grown radius and spawned the ball in the
    /// air. The prop reset comes last of the three, because it clears the candidate queue and the
    /// radial proxies the reset left behind.
    /// </para>
    /// <para>
    /// <b>Cells that cannot be measured are not failures.</b> A P2 check with no
    /// <see cref="PickupFieldHost"/> wired or with the host's prop obstacles switched off, a check whose readout is a console trace a human
    /// reads (<c>readout=trace</c>), and a check that needs the P4 step counters all report
    /// <c>BLOCKED_UNTIL_*</c> and are counted as skipped. P3 is in the tree, so a P3 check is
    /// measured, not skipped. Only a measured check can be <c>UNEXPECTED_*</c>.
    /// </para>
    /// <para>
    /// <b>Metrics are written in terms of the ball's collision radius and the cell's nominal
    /// dimension</b> (the plate thickness or the step rise / ramp top height), never in terms of a
    /// box's coordinates: that keeps one limit valid for all four diameters and keeps geometry in
    /// the placement file where it belongs. <c>deck_gap_m</c> is the minimum over the whole run of
    /// <c>|(centre.y - collisionRadius) - nominal|</c>, which reads 0 when the ball ever stood on
    /// the deck and <c>nominal</c> when it never did - exactly "did it climb". On a ramp cell the
    /// step-limit derivation of that expectation is skipped: the ramp exists to lift the ball above
    /// every step limit, and <see cref="PhysicsTestCell.IsRampClimb"/> is what says so.
    /// </para>
    /// </remarks>
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class PhysicsTestFieldDriver : UdonSharpBehaviour
    {
        // ---- links -------------------------------------------------------------

        [Header("Links")]
        [SerializeField] private KatamariBallMotor motor;
        [SerializeField] private PickupFieldHost pickupHost;
        [SerializeField] private LabBallSizeSetter ballSize;

        [Tooltip("Cells in pass order. Filled by the criteria importer from cells[] in order.")]
        [SerializeField] private PhysicsTestCell[] cells;
        [SerializeField] private float[] presetDiametersM;

        [Tooltip("Sent to the motor after every Reapply. Must contain the size setter's feeder kind " +
                 "and every prop kind in the field; the arena's own list is the cell's props only.")]
        [SerializeField] private string[] kindWhitelist;

        [Header("Optional")]
        [Tooltip("Trails the ball so an operator can watch the run. Null = no camera work.")]
        [SerializeField] private Transform chaseCamera;
        [SerializeField] private Vector3 chaseOffset = new Vector3(0f, 3.2f, -4.6f);

        [Header("Run parameters")]
        [SerializeField] private bool autoBeginAllOnStart = false;
        [SerializeField] private float settleSeconds = 0.5f;
        [SerializeField] private float sizeTimeoutSeconds = 3f;
        [SerializeField] private bool logTrajectory = true;
        [SerializeField] private float trajectoryIntervalSeconds = 0.5f;

        [Header("Manual sandbox (the one registration: every cell on one floor, one outer wall)")]
        [Tooltip("The sandbox's own arena: the big floor and the 4 outer boundary planes, no " +
                 "boxes. Filled by the criteria importer from LevelContent/SANDBOX. Null = the " +
                 "sandbox has no outer wall.")]
        [SerializeField] private LevelArenaHost sandboxFrame;
        [Tooltip("Where the ball is put when the sandbox opens (y is replaced by radius + 0.02). " +
                 "Filled by the criteria importer from the sandbox block of the criteria file.")]
        [SerializeField] private Vector3 sandboxSpawn;
        [SerializeField] private float sandboxSpawnHeadingDegrees;

        // A converged setter must also have actually resized the ball. A constant, not a serialized
        // field: a field added to an already-serialized UdonBehaviour reads 0 until the criteria are
        // re-imported, and a zero tolerance would block every run instead of guarding one.
        private const float SizeAppliedToleranceM = 0.02f;

        // Stamped into the first log line so "which U# program is running" is checkable. The
        // 2026-11-15 matrix ran a pre-P3 driver against post-P3 data and nobody could tell from
        // the log; the build line and the reason= vocabulary make that impossible to repeat.
        // 2026-11-17a: record-only criteria (op "record"), the climb / slope readings, the
        // per-diameter run mask.
        // 2026-11-17b: the manual sandbox (every cell registered at once, one outer frame);
        // the walk-around activation and the one-isolated-cell registration are gone.
        private const string DriverRevision = "2026-11-17b";
        // Op code of a diagnostic: the value is logged, never judged (the cell's op array).
        private const int OpRecord = 5;

        // Contact v2 thresholds (CONTACT_MODEL_V2 6.1/6.3/7.7.2). The slow-climb cap is
        // H_cap = ContactTuning.KHeight * (2r + lambda * L_eff); a cell of this field carries no
        // protruding prop where the cap is judged, so L_eff = 0 and H_cap = k_h * D. The bound
        // adds the design's 2% float-fade allowance and ContactTuning's cap_margin (the band below
        // H_cap where a climb counts as done), so no per-diameter threshold is typed into the data.
        private const float V2CapFloatMargin = 1.02f;

        // The slope rule's own numbers, mirrored from the motor's serialized defaults
        // (rampRestSpeedMps = 0.05 m/s at the start diameter). The slide mark is a threshold on
        // the downhill speed, not a motor state: the motor exposes no "sliding" getter.
        private const float RestSpeedMps = 0.05f;
        private const float SlideSpeedMps = 0.1f;
        // The window before the release over which the accepted centre speed is averaged.
        private const float PushTailSeconds = 2f;

        // Symbolic threshold codes, shared with the criteria importer (checkV2Thresholds).
        private const int ThresholdLimit = 0;
        private const int ThresholdClimbCapBound = 1;

        // ---- state -------------------------------------------------------------

        private const int StateIdle = 0;
        private const int StateSwitch = 1;
        private const int StateSize = 2;
        private const int StateSettle = 3;
        private const int StateDrive = 4;
        private const int StateReport = 5;

        private int state = StateIdle;
        private int cellIndex;
        private int diameterIndex;
        private int runIndex;
        private float stateClock;
        private float driveClock;
        private float runSeconds;
        private float driveEndSeconds;
        private float nextTrajectoryTime;

        // Why a metric could not be read. 0 is "read it"; every other code names a REAL metric
        // this run cannot answer, and only MetricUnknown means the NAME is wrong. The driver
        // reports the code's name in the ALERT so a setup gap never looks like a data typo
        // (design section 8.3.2).
        private const int MetricOk = 0;
        private const int MetricUnknown = 1;
        private const int MetricNoDeckSample = 2;
        private const int MetricNoLiftSample = 3;
        private const int MetricNoGateSample = 4;

        private bool pendingBegin;
        private int pendingCellIndex;
        private int pendingDiameterIndex;
        private float pendingBeginClock;

        private bool autoAdvance;
        private bool passActive;
        private float passStartTime;
        private int passRuns;
        private int passUnexpected;
        private int passSkipped;

        // ---- per-run samples ---------------------------------------------------

        private Vector3 startCenterWorld;
        private float runHeadingDegrees;
        private float baseOverflow;
        private int baseAbsorbed;
        private int baseAttached;
        private int baseRecovery;
        private int maxBlocking;
        private float maxLiftOverNominal;
        private bool hasLiftSample;
        // The same lift, accumulated only while the ball is over the cell's deck (design
        // CONTACT_MODEL_V2 7.7.4). A separate latch, not a second metric definition: v1 keeps
        // reading the whole-run maximum, v2's override row names this one.
        private float maxLiftInDeckOverNominal;
        private bool hasLiftInDeckSample;
        private float minDeckGap;
        private bool hasDeckSample;
        private float peakSpeedEarly;
        private float peakSpeedLate;
        private bool hasPrevCenter;
        private bool previousFalling;
        private float previousCenterY;
        private float maxFrameDy;
        private float maxGroundedFrameDy;
        private float maxCenterY;
        // Latched, not final: ramp_entry_progress_m asks whether the ball EVER got onto the ramp.
        private float maxRampEntryM;
        private int sawRampSupport;
        private int fallFrameCount;
        private float lastForwardIntent;
        private bool hasStopAnchor;
        private bool hasStopped;
        private float stopAnchorProgress;
        private float stoppedMinProgress;
        private float slipBack;
        private int rampCountAtSwitch;
        private int rampProbeReasonLength;
        private float rampProbeSlopeDegrees;

        // Climb and slope readings (record-only criteria of the wall + ceiling cell and the
        // gentle-slope cells). The drive plan names the marks: the release is the first row that
        // lets go after a push, the reverse is the first row that pulls back.
        private float releaseSeconds;
        private float reverseSeconds;
        private bool hasReleaseMark;
        private float releaseCenterY;
        private int climbActiveAtRelease;
        private int capLockAtRelease;
        private bool hasReverseMark;
        private float reverseStartCenterY;
        private float reverseStartProgress;
        private float climbActiveSeconds;
        private float climbLockSeconds;
        private float maxClimbHeightM;
        private float pushTailPath;
        private float pushTailTime;
        private Vector3 previousCenter;
        private float lastLateralIntent;
        private bool everPushed;
        private float flatDriftMax;
        private bool hasRestMark;
        private float restMarkSeconds;
        private bool hasSlideMark;
        private float slideMarkSeconds;
        private float peakDownhillSpeed;
        private float pushSpeedOnSlope;
        private float pushSpeedOnFlat;

        // Sandbox state: true once the sandbox (frame + every cell) is what the motor holds.
        private bool sandboxActive;
        private int sandboxEnterCount;

        // ---- per-run result ----------------------------------------------------

        private int checkPass;
        private int checkUnexpected;
        private int checkSkipped;
        private int checkDiagnostic;
        private string lastSummaryLine = "";

        // ---- public API --------------------------------------------------------

        public void Start()
        {
            // Nothing here touches the motor's STATE: its Start may not have run yet, and its stores
            // are allocated there. The first Update waits for a positive diameter instead of racing.
            // The one read below is safe before that: the cell's revision is a compile-time constant.
            int cellCount = cells == null ? 0 : cells.Length;
            Debug.Log("[ptf] build=" + DriverRevision + " cells=" + cellCount.ToString()
                + " presets=" + (presetDiametersM == null ? 0 : presetDiametersM.Length).ToString()
                + " cellRev=" + CellRevisionText()
                + " v2Rows=" + V2RowsText());
            // Every host starts SUSPENDED: its geometry stays visible, but it may not
            // self-register. LevelArenaHost registers in its own Update and the motor's tables are
            // global, so thirteen self-registering hosts would all start their surface ids at 1.
            // The driver registers the frame and then every cell once, with offset ids
            // (EnterSandbox).
            if (sandboxFrame != null) sandboxFrame.SuspendRegistration();
            if (cells != null)
            {
                for (int i = 0; i < cells.Length; i++)
                {
                    if (cells[i] != null) cells[i].ReleaseArena();
                }
            }

            if (autoBeginAllOnStart) BeginAll();
        }

        public void Update()
        {
            if (pendingBegin)
            {
                TickPendingBegin();
                return;
            }

            if (state == StateIdle)
            {
                if (!sandboxActive) TickSandboxEnter();
                return;
            }

            if (motor == null) return;

            switch (state)
            {
                case StateSwitch:
                    TickSwitch();
                    return;
                case StateSize:
                    TickSize();
                    return;
                case StateSettle:
                    TickSettle();
                    return;
                case StateDrive:
                    TickDrive();
                    return;
                case StateReport:
                    TickReport();
                    return;
                default:
                    state = StateIdle;
                    return;
            }
        }

        // ---- operator entry points --------------------------------------------

        /// <summary>Runs one cell at one diameter ("C10", 0.30). The main seam for a probe or MCP.</summary>
        public void BeginCellAtDiameter(string cellId, float diameterM)
        {
            int found = IndexOfCell(cellId);
            if (found < 0)
            {
                Debug.Log("[ptf] abort reason=unknownCell cell=" + cellId);
                return;
            }

            int diameter = IndexOfDiameter(diameterM);
            if (diameter < 0)
            {
                Debug.Log("[ptf] abort reason=unknownDiameter diameter=" + diameterM);
                return;
            }

            autoAdvance = false;
            BeginFromIndex(found, diameter);
        }

        /// <summary>Runs one cell at all four preset diameters, in preset order.</summary>
        public void BeginCell(string cellId)
        {
            int found = IndexOfCell(cellId);
            if (found < 0)
            {
                Debug.Log("[ptf] abort reason=unknownCell cell=" + cellId);
                return;
            }

            autoAdvance = true;
            StartPass();
            BeginFromIndex(found, 0);
        }

        /// <summary>Runs every cell at every diameter. The whole pass; one run per cell and preset.</summary>
        public void BeginAll()
        {
            if (cells == null || cells.Length == 0)
            {
                Debug.Log("[ptf] abort reason=noCells");
                return;
            }

            autoAdvance = true;
            StartPass();
            BeginFromIndex(0, 0);
        }

        /// <summary>Stops after the current step. The arena stays registered where it is.</summary>
        public void Abort()
        {
            if (state != StateIdle)
            {
                Debug.Log("[ptf] abort run=" + runIndex + " state=" + state);
            }

            autoAdvance = false;
            passActive = false;
            pendingBegin = false;
            state = StateIdle;
            if (motor != null) motor.SetMoveAxes(0f, 0f, 0f);
        }

        /// <summary>Programmatic form: cell index (pass order) and diameter index (preset order).</summary>
        public void BeginFromIndex(int newCellIndex, int newDiameterIndex)
        {
            if (cells == null || newCellIndex < 0 || newCellIndex >= cells.Length) return;
            if (presetDiametersM == null || newDiameterIndex < 0 ||
                newDiameterIndex >= presetDiametersM.Length) return;
            if (cells[newCellIndex] == null) return;

            pendingCellIndex = newCellIndex;
            pendingDiameterIndex = newDiameterIndex;
            pendingBegin = true;
            pendingBeginClock = 0f;
        }

        // ---- status getters ----------------------------------------------------

        public bool IsRunning()
        {
            return state != StateIdle || pendingBegin;
        }

        public string GetCurrentCellId()
        {
            if (cells == null || cellIndex < 0 || cellIndex >= cells.Length) return "";
            if (cells[cellIndex] == null) return "";
            return cells[cellIndex].GetCellId();
        }

        public float GetCurrentDiameterM()
        {
            if (presetDiametersM == null || diameterIndex < 0 ||
                diameterIndex >= presetDiametersM.Length) return 0f;
            return presetDiametersM[diameterIndex];
        }

        /// <summary>Run number since the scene started (never reset: it keys the round id).</summary>
        public int GetRunIndex()
        {
            return runIndex;
        }

        public int GetRunCount()
        {
            if (cells == null || presetDiametersM == null) return 0;
            return cells.Length * presetDiametersM.Length;
        }

        /// <summary>Checks that were measured and matched their expectation in the last run.</summary>
        public int GetExpectedCount()
        {
            return checkPass;
        }

        public int GetUnexpectedCount()
        {
            return checkUnexpected;
        }

        public int GetSkippedCount()
        {
            return checkSkipped;
        }

        public string GetLastSummaryLine()
        {
            if (lastSummaryLine == null) return "";
            return lastSummaryLine;
        }

        public int GetCellCount()
        {
            if (cells == null) return 0;
            return cells.Length;
        }

        public string GetCellIdAt(int index)
        {
            if (cells == null || index < 0 || index >= cells.Length) return "";
            if (cells[index] == null) return "";
            return cells[index].GetCellId();
        }

        // ---- passage / run bootstrap ------------------------------------------

        private void StartPass()
        {
            passActive = true;
            passRuns = 0;
            passUnexpected = 0;
            passSkipped = 0;
            passStartTime = Time.time;
            // runIndex is deliberately NOT reset: it is the round id's key, and
            // TryResetForNewRound refuses an id the current round already uses. A second pass in
            // one session therefore continues the numbering instead of replaying round "1".
        }

        private void TickPendingBegin()
        {
            pendingBeginClock = pendingBeginClock + Time.deltaTime;

            if (motor == null)
            {
                pendingBegin = false;
                return;
            }

            // The motor builds its stores in Start; a positive semantic diameter is its public
            // "ready" proof (the same gate LevelArenaHost uses).
            if (motor.GetSemanticDiameterWorldM() <= 0f)
            {
                if (pendingBeginClock > 10f)
                {
                    Debug.Log("[ptf] abort reason=motorNeverReady");
                    pendingBegin = false;
                    autoAdvance = false;
                }
                return;
            }

            int newCellIndex = pendingCellIndex;
            int newDiameterIndex = pendingDiameterIndex;
            pendingBegin = false;
            cellIndex = newCellIndex;
            diameterIndex = newDiameterIndex;
            runIndex = runIndex + 1;
            ResetRunSamples();
            state = StateSwitch;
            stateClock = 0f;
        }

        /// <summary>
        /// Manual sandbox: waits for the motor, then registers every cell's boards at once.
        /// </summary>
        private void TickSandboxEnter()
        {
            if (motor == null || cells == null) return;
            // Same readiness proof as the scripted runs: the motor builds its stores in Start.
            if (motor.GetSemanticDiameterWorldM() <= 0f) return;
            EnterSandbox();
        }

        /// <summary>
        /// The one registration: the frame host (<c>LevelArenaHost.Register</c>) clears the motor
        /// and registers the big floor and the 4 outer planes, then every cell appends its boards
        /// (a cell has no floor and no wall of its own) with surface ids offset by what the cells
        /// before it registered (the contact solver de-duplicates by surface id).
        /// </summary>
        private void EnterSandbox()
        {
            int boxes = 0;
            int ramps = 0;
            if (sandboxFrame != null)
            {
                sandboxFrame.Register(true, 0, 0);
            }
            else
            {
                motor.ClearObstacles();
                Debug.LogWarning("[ptf] sandbox has no frame host: no outer boundary wall");
            }

            for (int i = 0; i < cells.Length; i++)
            {
                if (cells[i] == null) continue;
                cells[i].ApplySandboxRegistration(boxes, ramps);
                boxes = boxes + cells[i].GetRegisteredBoxCount();
                ramps = ramps + cells[i].GetRegisteredRampCount();
            }

            if (kindWhitelist != null)
            {
                motor.SetAllowedKinds(kindWhitelist, kindWhitelist.Length);
            }

            // Reset first, then read the radius (the order TickSwitch documents), then place.
            sandboxEnterCount = sandboxEnterCount + 1;
            int reason = -1;
            string roundId = "ptf-sandbox-" + sandboxEnterCount.ToString();
            bool reset = motor.TryResetForNewRound(roundId, sandboxSpawn, sandboxSpawnHeadingDegrees,
                out reason);
            float radius = motor.GetCollisionRadiusWorldM();
            if (radius <= 0f) radius = 0.15f;
            motor.TeleportTo(new Vector3(sandboxSpawn.x, radius + 0.02f, sandboxSpawn.z));
            motor.SetMoveAxes(0f, 0f, 0f);
            if (pickupHost != null) pickupHost.ResetField();

            sandboxActive = true;
            int framePlanes = sandboxFrame == null ? 0 : sandboxFrame.GetRegisteredPlaneCount();
            Log("[ptf] sandbox cells=" + cells.Length.ToString()
                + " boxes=" + boxes.ToString() + " ramps=" + ramps.ToString()
                + " obbs=" + motor.GetStaticObbCount().ToString()
                + "/" + motor.GetStaticObbCapacity().ToString()
                + " obbOverflow=" + motor.GetStaticObbOverflowCount().ToString()
                + " framePlanes=" + framePlanes.ToString()
                + " reset=" + (reset ? "ok" : reason.ToString())
                + " spawn=(" + Fixed(sandboxSpawn.x, 2) + "," + Fixed(sandboxSpawn.z, 2) + ")"
                + " hdg=" + Fixed(sandboxSpawnHeadingDegrees, 1));
        }

        private void TickSwitch()
        {
            PhysicsTestCell cell = cells[cellIndex];
            if (cell == null)
            {
                Blocked("noCell");
                return;
            }

            // A cell that declares it cannot host the ball at this diameter (a ceiling lower than
            // the ball) is not driven: the ball would start inside the slab and every reading would
            // describe the penetration push, not the cell.
            if (!cell.RunsAtDiameter(diameterIndex))
            {
                Log("[ptf] skipped run=" + runIndex + " cell=" + cell.GetCellId()
                    + " D=" + Fixed(presetDiametersM[diameterIndex], 3)
                    + " reason=cell_does_not_run_at_this_diameter verdict=SKIPPED_NOT_RUN");
                FinishRun();
                return;
            }

            // The override rows are DATA the importer bakes into the scene, not a driver rule. A
            // cell baked before the tables existed has none, so HasCheckV2() answers false for
            // every check and the run silently judges every override row as its base row - the
            // 2026-11-16 O3a regression, where a working ball produced 31 "unexpected" verdicts.
            // Refuse to publish a matrix that was judged under rows the field does not use: this
            // is a setup error, and it is loud.
            if (cell.GetCheckCount() > 0 && cell.GetCheckV2RowCount() < 0)
            {
                Debug.LogError("[ptf] abort reason=staleCriteriaV2 cell=" + cell.GetCellId()
                    + " v2Rows=missing: this cell was baked before the criteria carried override "
                    + "rows, so every override row would be judged as its base row. Re-import the "
                    + "criteria (VRC Katamari/Physics Test Field/Import Criteria) and SAVE the "
                    + "scene, then rerun; the import must print the override-row count and this "
                    + "build line must repeat it.");
                autoAdvance = false;
                passActive = false;
                state = StateIdle;
                Debug.Log("[ptf] field aborted reason=staleCriteriaV2");
                return;
            }

            // A scripted run does not change the registration: every cell is already in the
            // motor (the sandbox). It only moves the ball to this cell's start. The first run of
            // a session that began before the sandbox opened registers it here.
            if (!sandboxActive) EnterSandbox();

            // The sandbox has registered the ramps (or refused them at bake time); read that
            // count before the probe so it describes what the ARENAS put in the motor. Then, for
            // a cell that authors an over-limit board (C06), ask the motor to register the
            // board's walking face directly: the refusal IS the criterion, and it is the only
            // way the motor's refusal getters can be exercised, because LevelArenaHost's bake
            // refuses an over-limit board before it ever calls AddRamp.
            rampCountAtSwitch = motor.GetRampCount();
            ApplyRampProbe(cell);

            // A ramp cell must be marked as one, or the deck derivation below treats its top deck as
            // a step and the whole ramp group reads as "should be blocked". The flag comes from the
            // criteria import (placement role=ramp), so a mismatch is a stale import, not physics.
            if (rampCountAtSwitch > 0 && !cell.IsRampClimb())
            {
                Debug.LogWarning("[ptf] rampCellMismatch cell=" + cell.GetCellId()
                    + " ramps=" + rampCountAtSwitch.ToString()
                    + " rampClimb=0; re-import the criteria (the deck derivation would treat the deck as a step)");
            }
            else if (cell.IsRampClimb() && rampCountAtSwitch == 0)
            {
                Debug.LogWarning("[ptf] rampCellMismatch cell=" + cell.GetCellId()
                    + " ramps=0 rampClimb=1; the arena registered no ramp, so ramp_support/ramp_count will fail");
            }

            // Baselines are read AFTER the switch and BEFORE anything else moves: the overflow
            // latch and the contact-saturation flag are permanent, so only a delta is meaningful.
            baseOverflow = motor.GetOverflowCount();
            baseAbsorbed = motor.GetAbsorbedCount();
            baseRecovery = motor.GetRecoveryCount();
            baseAttached = 0;
            if (pickupHost != null) baseAttached = pickupHost.GetAttachedCount();

            Vector3 authored = cell.GetStartCenter();
            float heading = cell.GetStartHeadingDegrees();
            runHeadingDegrees = heading;

            string roundId = "ptf-" + cell.GetCellId() + "-" + runIndex.ToString();
            int reason = -1;
            // Reset FIRST, then read the radius. TryResetForNewRound restores the round's
            // baseline size, so a radius read AFTER it is the radius the ball actually has
            // when the pose is applied. The old order read it BEFORE the reset, i.e. the
            // PREVIOUS run's grown radius: after a 2.2 m run the next run spawned at
            // y = 1.12 instead of 0.17, fell from the air, and the step mask called a
            // 0.075 m deck "already above" (C10 was not blocked in a full BeginAll pass).
            // The reset is given the cell's authored pose; the teleport below replaces its
            // height with the radius-derived one.
            bool reset = motor.TryResetForNewRound(roundId, authored, heading, out reason);
            if (!reset)
            {
                Debug.Log("[ptf] blocked run=" + runIndex + " cell=" + cell.GetCellId()
                    + " stage=reset reason=" + reason
                    + " verdict=BLOCKED_UNTIL_reset");
                checkSkipped = checkSkipped + 1;
                passSkipped = passSkipped + 1;
                FinishRun();
                return;
            }

            float radius = motor.GetCollisionRadiusWorldM();
            if (radius <= 0f) radius = 0.15f;
            Vector3 pose = new Vector3(authored.x, radius + 0.02f, authored.z);
            motor.TeleportTo(pose);

            if (pickupHost != null) pickupHost.ResetField();

            startCenterWorld = motor.GetCenterWorld();
            driveEndSeconds = cell.GetDriveEndSeconds();
            runSeconds = driveEndSeconds + cell.GetStopSeconds();
            if (runSeconds < 0.5f) runSeconds = 0.5f;
            ComputeDriveMarks(cell);
            previousCenter = startCenterWorld;

            Log(
                "[ptf] run=" + runIndex
                + " cell=" + cell.GetCellId()
                + " D=" + Fixed(presetDiametersM[diameterIndex], 3)
                + " start=(" + Fixed(startCenterWorld.x, 3) + "," + Fixed(startCenterWorld.y, 3)
                + "," + Fixed(startCenterWorld.z, 3) + ")"
                + " hdg=" + Fixed(heading, 1)
                + " arena=" + ArenaReport(cell)
                + " gates=" + GateSummary(cell));

            // The ONE size request of this run. It goes here (after the reset re-priced the ball to
            // the baseline) and nowhere else: TickSize only observes IsConverged/HasFailed. Two
            // earlier shapes failed - a "same target" short-circuit kept the previous run's
            // converged state (only the first cell per tier was resized), and re-issuing the
            // request from the poll loop cleared converged once per frame (the size stage timed out
            // on a ball already at the target).
            if (ballSize != null) ballSize.SetDiameterM(presetDiametersM[diameterIndex]);

            state = StateSize;
            stateClock = 0f;
        }

        private void ApplyRampProbe(PhysicsTestCell cell)
        {
            rampProbeReasonLength = 0;
            rampProbeSlopeDegrees = 0f;
            if (cell == null) return;
            if (!cell.HasRampProbe()) return;

            bool accepted = motor.AddRamp(cell.GetRampProbeCenter(), cell.GetRampProbeNormal(),
                cell.GetRampProbeRight(), cell.GetRampProbeHalfWidth(),
                cell.GetRampProbeHalfRun());
            if (accepted)
            {
                // The probe exists to be REFUSED. An accepted probe means the authored board
                // and the motor's slope guard disagree; drop it again so the support table is
                // exactly the arena's, then re-apply the arena (the ClearRamps call is
                // deliberately narrower than ClearObstacles).
                motor.ClearRamps();
                rampCountAtSwitch = motor.GetRampCount();
                Debug.LogError("[ptf] rampProbe cell=" + cell.GetCellId()
                    + " accepted but should be refused; the board and the slope guard disagree");
                return;
            }

            string reason = motor.GetLastRampRefusalReason();
            if (reason != null) rampProbeReasonLength = reason.Length;
            rampProbeSlopeDegrees = motor.GetLastRampRefusalSlopeDegrees();
            Log("[ptf] rampProbe cell=" + cell.GetCellId()
                + " refused=1 slope=" + Fixed(rampProbeSlopeDegrees, 2)
                + " reason=" + reason);
        }

        private void TickSize()
        {
            PhysicsTestCell cell = cells[cellIndex];
            if (cell == null)
            {
                Blocked("noCell");
                return;
            }

            float target = presetDiametersM[diameterIndex];
            stateClock = stateClock + Time.deltaTime;

            if (ballSize == null)
            {
                // Without a setter only the baseline diameter is reachable; at 0.30 that is still a
                // valid run, and at the other presets it is a setup gap, not a physics failure.
                float delta = motor.GetSemanticDiameterWorldM() - target;
                if (delta < 0f) delta = -delta;
                if (delta > 0.02f)
                {
                    Debug.Log("[ptf] blocked run=" + runIndex + " cell=" + cell.GetCellId()
                        + " stage=size reason=noSetter target=" + Fixed(target, 3)
                        + " verdict=BLOCKED_UNTIL_setter");
                    checkSkipped = checkSkipped + 1;
                    passSkipped = passSkipped + 1;
                    FinishRun();
                    return;
                }

                state = StateSettle;
                stateClock = 0f;
                return;
            }

            // Read-only from here on: the request was issued once in Switch. Calling SetDiameterM
            // here would clear converged every frame and the stage would time out.
            if (ballSize.IsConverged())
            {
                // Converged must mean "the ball IS this diameter", not only "the setter stopped
                // feeding". A stale program or a skipped request used to report converged with the
                // ball still at the baseline, and the whole matrix silently measured the wrong
                // ball; that case is a BLOCKED setup, not a physics result.
                float applied = motor.GetSemanticDiameterWorldM();
                float appliedGap = applied - target;
                if (appliedGap < 0f) appliedGap = -appliedGap;
                if (appliedGap > SizeAppliedToleranceM)
                {
                    Debug.Log("[ptf] blocked run=" + runIndex + " cell=" + cell.GetCellId()
                        + " stage=size reason=sizeNotApplied target=" + Fixed(target, 3)
                        + " diameter=" + Fixed(applied, 3)
                        + " verdict=BLOCKED_UNTIL_size");
                    checkSkipped = checkSkipped + 1;
                    passSkipped = passSkipped + 1;
                    FinishRun();
                    return;
                }

                state = StateSettle;
                stateClock = 0f;
                return;
            }

            if (ballSize.HasFailed())
            {
                Debug.Log("[ptf] blocked run=" + runIndex + " cell=" + cell.GetCellId()
                    + " stage=size reason=" + ballSize.GetLastFailure()
                    + " target=" + Fixed(target, 3)
                    + " verdict=BLOCKED_UNTIL_size");
                checkSkipped = checkSkipped + 1;
                passSkipped = passSkipped + 1;
                FinishRun();
                return;
            }

            if (stateClock > sizeTimeoutSeconds)
            {
                Debug.Log("[ptf] blocked run=" + runIndex + " cell=" + cell.GetCellId()
                    + " stage=size reason=timeout target=" + Fixed(target, 3)
                    + " diameter=" + Fixed(motor.GetSemanticDiameterWorldM(), 3)
                    + " verdict=BLOCKED_UNTIL_size");
                checkSkipped = checkSkipped + 1;
                passSkipped = passSkipped + 1;
                FinishRun();
            }
        }

        private void TickSettle()
        {
            stateClock = stateClock + Time.deltaTime;
            if (stateClock < settleSeconds) return;

            driveClock = 0f;
            stateClock = 0f;
            nextTrajectoryTime = trajectoryIntervalSeconds;
            state = StateDrive;
        }

        private void TickDrive()
        {
            PhysicsTestCell cell = cells[cellIndex];
            if (cell == null)
            {
                Blocked("noCell");
                return;
            }

            driveClock = driveClock + Time.deltaTime;

            ApplyDriveAxes(cell, driveClock);
            Sample(cell);

            if (logTrajectory && driveClock >= nextTrajectoryTime)
            {
                nextTrajectoryTime = nextTrajectoryTime + trajectoryIntervalSeconds;
                LogTrajectory(driveClock);
            }

            FollowBall();

            if (driveClock < runSeconds) return;

            motor.SetMoveAxes(0f, 0f, 0f);
            // One last sample so the readings describe the stopped ball, not the frame before it.
            Sample(cell);
            state = StateReport;
        }

        private void TickReport()
        {
            PhysicsTestCell cell = cells[cellIndex];
            if (cell == null)
            {
                Blocked("noCell");
                return;
            }

            EvaluateChecks(cell);
            FinishRun();
        }

        private void FinishRun()
        {
            motor.SetMoveAxes(0f, 0f, 0f);
            passRuns = passRuns + 1;
            passUnexpected = passUnexpected + checkUnexpected;
            passSkipped = passSkipped + checkSkipped;
            state = StateIdle;

            if (!autoAdvance || !passActive) return;

            int nextDiameter = diameterIndex + 1;
            int nextCell = cellIndex;
            if (nextDiameter >= presetDiametersM.Length)
            {
                nextDiameter = 0;
                nextCell = nextCell + 1;
            }

            if (nextCell >= cells.Length)
            {
                autoAdvance = false;
                LogFieldSummary();
                return;
            }

            BeginFromIndex(nextCell, nextDiameter);
        }

        // ---- driving and sampling ---------------------------------------------

        private void ApplyDriveAxes(PhysicsTestCell cell, float seconds)
        {
            int count = cell.GetDriveCount();
            float forward = 0f;
            float turn = 0f;
            float lateral = 0f;
            bool found = false;
            for (int i = 0; i < count; i++)
            {
                if (cell.GetDriveTime(i) > seconds) break;
                forward = cell.GetDriveForward(i);
                turn = cell.GetDriveTurn(i);
                lateral = cell.GetDriveLateral(i);
                found = true;
            }

            if (!found)
            {
                lastForwardIntent = 0f;
                lastLateralIntent = 0f;
                motor.SetMoveAxes(0f, 0f, 0f);
                return;
            }

            lastForwardIntent = forward;
            lastLateralIntent = lateral;
            if (forward != 0f || lateral != 0f) everPushed = true;
            motor.SetMoveAxes(forward, turn, lateral);
        }

        private void Sample(PhysicsTestCell cell)
        {
            Vector3 center = motor.GetCenterWorld();
            float radius = motor.GetCollisionRadiusWorldM();
            float nominal = cell.GetNominalM();
            float bottom = center.y - radius;

            float deckGap = bottom - nominal;
            if (deckGap < 0f) deckGap = -deckGap;
            if (!hasDeckSample || deckGap < minDeckGap) minDeckGap = deckGap;
            hasDeckSample = true;

            float lift = bottom - nominal;
            if (!hasLiftSample || lift > maxLiftOverNominal) maxLiftOverNominal = lift;
            hasLiftSample = true;

            // The v2 plate criterion (CONTACT_MODEL_V2 7.7.4): a thin plate must not lift the ball,
            // but a run that keeps pushing at the far boundary wall legitimately climbs it. So the
            // gated latch only takes samples while the ball's XZ is over THIS cell's deck, grown by
            // one collision radius. Both numbers are data: the deck footprint the criteria importer
            // baked from the arena collider, and the ball's own radius.
            if (cell.IsInDeckGate(center.x, center.z, radius))
            {
                if (!hasLiftInDeckSample || lift > maxLiftInDeckOverNominal)
                {
                    maxLiftInDeckOverNominal = lift;
                }

                hasLiftInDeckSample = true;
            }

            if (center.y > maxCenterY) maxCenterY = center.y;

            Vector3 velocity = motor.GetPlanarVelocity();
            float speed = Mathf.Sqrt(velocity.x * velocity.x + velocity.z * velocity.z);
            if (driveClock <= driveEndSeconds * 0.25f && speed > peakSpeedEarly)
            {
                peakSpeedEarly = speed;
            }
            if (driveClock >= driveEndSeconds * 0.6f && speed > peakSpeedLate)
            {
                peakSpeedLate = speed;
            }

            if (pickupHost != null)
            {
                int blocking = pickupHost.GetBlockingPropCount();
                if (blocking > maxBlocking) maxBlocking = blocking;
            }

            SampleDiagnostics(cell, center, radius, speed, velocity);

            bool falling = motor.IsFalling();
            if (hasPrevCenter)
            {
                float dy = center.y - previousCenterY;
                float absDy = dy;
                if (absDy < 0f) absDy = -absDy;
                if (absDy > maxFrameDy) maxFrameDy = absDy;
                // The seam check must not read the free fall as a "step": the ramp cell's drop
                // is tens of centimetres in one frame, while a seam is millimetres. Both ends of
                // the frame must be grounded, otherwise the landing frame (already not falling,
                // but the previous sample was mid-air) would enter as a huge "seam" step.
                if (!falling && !previousFalling && absDy > maxGroundedFrameDy)
                {
                    maxGroundedFrameDy = absDy;
                }
            }

            hasPrevCenter = true;
            previousFalling = falling;
            previousCenterY = center.y;

            // Ramp and landing readings. "Saw a ramp" is a per-frame latch: support_kind is
            // instantaneous, and a run that climbs and then stops on the top landing would
            // otherwise report "box" and look like the ramp never engaged.
            if (motor.GetSupportKind() == 2) sawRampSupport = 1;
            if (cell.IsRampClimb())
            {
                float rampEntry = ProgressMeters() - cell.GetRampFootM();
                if (rampEntry > maxRampEntryM) maxRampEntryM = rampEntry;
            }
            if (falling) fallFrameCount = fallFrameCount + 1;

            // Slip on a ramp, measured in EVERY released window (the drive plan deliberately
            // stops mid-slope for 5 s). The anchor is the last frame that was still moving;
            // once the ball is stopped ON A RAMP (support kind 2), only progress DECREASING
            // counts. That is the downhill distance after the stop; tangential gravity is off
            // by default, so it should be 0. A stop on the flat landing (kind 1) is not a
            // slip test and does not enter the reading.
            if (lastForwardIntent != 0f)
            {
                hasStopAnchor = false;
                hasStopped = false;
            }
            else
            {
                float progress = ProgressMeters();
                if (speed > 0.10f)
                {
                    stopAnchorProgress = progress;
                    hasStopAnchor = true;
                    hasStopped = false;
                }
                else if (hasStopAnchor && motor.GetSupportKind() == 2)
                {
                    if (!hasStopped)
                    {
                        hasStopped = true;
                        stoppedMinProgress = stopAnchorProgress;
                    }

                    if (progress < stoppedMinProgress) stoppedMinProgress = progress;
                    float slip = stopAnchorProgress - stoppedMinProgress;
                    if (slip > slipBack) slipBack = slip;
                }
            }
        }

        /// <summary>
        /// The drive plan's two marks: the first row that lets go after a push (the release) and
        /// the first row that pulls back after it (the reverse). -1 = the plan has none.
        /// </summary>
        private void ComputeDriveMarks(PhysicsTestCell cell)
        {
            releaseSeconds = -1f;
            reverseSeconds = -1f;
            bool pushed = false;
            int count = cell.GetDriveCount();
            for (int i = 0; i < count; i++)
            {
                float forward = cell.GetDriveForward(i);
                float lateral = cell.GetDriveLateral(i);
                float t = cell.GetDriveTime(i);
                if (forward != 0f || lateral != 0f)
                {
                    pushed = true;
                    if (forward < 0f && releaseSeconds >= 0f && reverseSeconds < 0f)
                    {
                        reverseSeconds = t;
                    }

                    continue;
                }

                if (pushed && releaseSeconds < 0f) releaseSeconds = t;
            }
        }

        /// <summary>
        /// Readings that only the record-only criteria use: the climb channel (is it active, is
        /// the cap lock set, how high did it get, how fast did the accepted centre actually move),
        /// what happened after the release and the reverse, and the slope rule (flat drift, rest,
        /// then slide, and the speed while pushing on a slope against the same push on the flat).
        /// </summary>
        private void SampleDiagnostics(PhysicsTestCell cell, Vector3 center, float radius,
            float speed, Vector3 velocity)
        {
            float frameDt = Time.deltaTime;
            bool pushing = lastForwardIntent != 0f || lastLateralIntent != 0f;

            if (motor.IsClimbing()) climbActiveSeconds = climbActiveSeconds + frameDt;
            if (motor.IsClimbLocked()) climbLockSeconds = climbLockSeconds + frameDt;
            float climbHeight = motor.GetClimbHeightWorldM();
            if (climbHeight > maxClimbHeightM) maxClimbHeightM = climbHeight;

            // Accepted centre movement per frame: the planar velocity says what the ball WANTS,
            // this says what the contact solver let it do (a wall climb has zero planar speed).
            float moved = (center - previousCenter).magnitude;
            previousCenter = center;
            if (pushing && releaseSeconds >= 0f && driveClock < releaseSeconds
                && driveClock >= releaseSeconds - PushTailSeconds)
            {
                pushTailPath = pushTailPath + moved;
                pushTailTime = pushTailTime + frameDt;
            }

            if (releaseSeconds >= 0f && !hasReleaseMark && driveClock >= releaseSeconds)
            {
                hasReleaseMark = true;
                releaseCenterY = center.y;
                climbActiveAtRelease = motor.IsClimbing() ? 1 : 0;
                capLockAtRelease = motor.IsClimbLocked() ? 1 : 0;
            }

            if (reverseSeconds >= 0f && !hasReverseMark && driveClock >= reverseSeconds)
            {
                hasReverseMark = true;
                reverseStartCenterY = center.y;
                reverseStartProgress = ProgressMeters();
            }

            // Slope rule. "On a slope" is the support normal's tilt; the downhill direction is
            // its horizontal part (a tilted normal leans downhill).
            Vector3 normal = motor.GetSupportNormalWorld();
            float normalXZ = Mathf.Sqrt(normal.x * normal.x + normal.z * normal.z);
            bool onSlope = normalXZ > 0.01f && normal.y > 0.5f;
            float sizeScale = presetDiametersM[0] > 0f
                ? motor.GetSemanticDiameterWorldM() / presetDiametersM[0] : 1f;

            if (!everPushed)
            {
                float dx = center.x - startCenterWorld.x;
                float dz = center.z - startCenterWorld.z;
                float drift = Mathf.Sqrt(dx * dx + dz * dz);
                if (drift > flatDriftMax) flatDriftMax = drift;
            }

            if (pushing)
            {
                if (onSlope)
                {
                    if (speed > pushSpeedOnSlope) pushSpeedOnSlope = speed;
                }
                else if (speed > pushSpeedOnFlat)
                {
                    pushSpeedOnFlat = speed;
                }
            }

            if (hasReleaseMark && !pushing)
            {
                if (!hasRestMark && speed < RestSpeedMps * sizeScale)
                {
                    hasRestMark = true;
                    restMarkSeconds = driveClock;
                }

                if (hasRestMark && onSlope)
                {
                    float downhill = (velocity.x * normal.x + velocity.z * normal.z) / normalXZ;
                    if (downhill > peakDownhillSpeed) peakDownhillSpeed = downhill;
                    if (!hasSlideMark && downhill > SlideSpeedMps * sizeScale)
                    {
                        hasSlideMark = true;
                        slideMarkSeconds = driveClock;
                    }
                }
            }
        }

        private void FollowBall()
        {
            if (chaseCamera == null) return;
            Vector3 center = motor.GetCenterWorld();
            chaseCamera.position = center + chaseOffset;
        }

        // ---- judgement ---------------------------------------------------------

        private void EvaluateChecks(PhysicsTestCell cell)
        {
            checkPass = 0;
            checkUnexpected = 0;
            checkSkipped = 0;
            checkDiagnostic = 0;
            string notApplicableList = "";

            int total = cell.GetCheckCount();
            for (int i = 0; i < total; i++)
            {
                string checkId = cell.GetCheckId(i);
                int op = cell.GetCheckOp(i);
                float limit = cell.GetCheckLimit(i);
                int readout = cell.GetCheckReadout(i);
                int gateStage = cell.GetCheckGateStage(i);
                string metric = cell.GetCheckMetric(i);
                int expect = cell.GetCheckExpect(i, diameterIndex);
                int threshold = ThresholdLimit;

                // Override rows (design section 5.6). When this criterion carries an override,
                // judge the override; otherwise the base row is the criterion. The override is
                // data, not a second rule: it may replace the metric, the op with its own limit, or
                // name a threshold the driver derives, plus the per-diameter expectation. It is
                // judged exactly as written - the two base-row derivations below never touch it.
                bool v2Row = cell.HasCheckV2(i);
                if (v2Row)
                {
                    string v2Metric = cell.GetCheckV2Metric(i);
                    if (v2Metric != null && v2Metric.Length > 0) metric = v2Metric;
                    int v2Op = cell.GetCheckV2Op(i);
                    if (v2Op >= 0)
                    {
                        // An op override carries its own limit: the pair is one decision.
                        op = v2Op;
                        limit = cell.GetCheckV2Limit(i);
                    }

                    int v2Expect = cell.GetCheckV2Expect(i, diameterIndex);
                    if (v2Expect >= 0) expect = v2Expect;
                    threshold = cell.GetCheckV2Threshold(i);
                }

                // The ramp-entry threshold is one STEP-LIMIT of travel, not an absolute 0.30 m
                // (design section 4.1.4): the motor's own rule makes stepLimit = 0.25 * D, and a
                // ball that has advanced that far horizontally onto the ramp has provably crossed
                // the foot (an invisible wall would hold it at ~0). The old 0.30 m sat 0.4 mm
                // above the 45-degree ramp's geometric maximum at D=0.60, so it could never pass.
                if (!v2Row && Same(metric, "ramp_entry_progress_m"))
                {
                    float entryMin = motor.GetStepHeightLimitWorldM();
                    if (entryMin > 0f)
                    {
                        limit = entryMin;
                        Log("[ptf] derived cell=" + cell.GetCellId() + " check=" + checkId
                            + " step_limit=" + Fixed(entryMin, 6)
                            + " min_progress=" + Fixed(entryMin, 6));
                    }
                }

                // Symbolic WALL height thresholds. C06 is a stamina slope and uses its
                // authored top-height criterion instead, never this wall cap.
                if (v2Row && threshold == ThresholdClimbCapBound)
                {
                    limit = ClimbCapBoundM();
                    Log("[ptf] derived cell=" + cell.GetCellId() + " check=" + checkId
                        + " metric=" + metric
                        + " h_cap=" + Fixed(ClimbCapM(), 4)
                        + " cap_bound=" + Fixed(limit, 4));
                }

                string expectText = ExpectText(expect);

                // Applicability comes before every other skip: if the SCENE cannot produce this
                // criterion's scenario at this diameter (design section 4.1.4), saying so is more
                // honest than a measurement that cannot exist. Counted as skipped, never as a pass.
                if (!cell.CheckApplies(i, diameterIndex))
                {
                    LogSkipped(checkId, metric, op, limit, expectText, "SKIPPED_NOT_APPLICABLE");
                    checkSkipped = checkSkipped + 1;
                    if (notApplicableList.Length > 0) notApplicableList = notApplicableList + ",";
                    notApplicableList = notApplicableList + checkId;
                    continue;
                }

                // Prop solids only reach the motor through PickupFieldHost. Without a host - or
                // with its prop obstacles switched off (it clears the set) - the P2 readings (what supports
                // the ball, how many prop tops are registered) can never leave their defaults,
                // so a gate=P2 check is unmeasurable rather than failed.
                if (gateStage == 2 && (pickupHost == null || !pickupHost.IsBlockingEnabled()))
                {
                    LogSkipped(checkId, metric, op, limit, expectText, "BLOCKED_UNTIL_P2");
                    checkSkipped = checkSkipped + 1;
                    continue;
                }

                // P3 is in the tree (AddRamp plus its refusal codes), so a P3 check is measured
                // like any other. The token stays in the data vocabulary but is no longer a skip
                // condition: a build without ramps would report 0 ramps and fail the check,
                // which is the honest answer.

                if (readout == 1)
                {
                    // A [ProxyTrace] window is read from the console by a human; the driver cannot
                    // parse its own log, so this is "not measured", never "failed".
                    LogSkipped(checkId, metric, op, limit, expectText, "BLOCKED_UNTIL_manual");
                    checkSkipped = checkSkipped + 1;
                    continue;
                }

                if (readout == 2)
                {
                    LogSkipped(checkId, metric, op, limit, expectText, "BLOCKED_UNTIL_P4");
                    checkSkipped = checkSkipped + 1;
                    continue;
                }

                if (op == OpRecord)
                {
                    // A diagnostic: read it, print it, never judge it. It is neither a pass nor
                    // an unexpected result, and it does not count as skipped either.
                    float recorded = 0f;
                    int recordStatus = TryReadMetric(metric, out recorded);
                    if (recordStatus != MetricOk)
                    {
                        Debug.Log("[ptf] check=" + checkId + " metric=" + metric
                            + " value=n/a op=record raw=NOT_READ verdict=DIAGNOSTIC reason="
                            + MetricReason(recordStatus));
                    }
                    else
                    {
                        Debug.Log("[ptf] check=" + checkId + " metric=" + metric
                            + " value=" + Signed(recorded, 4)
                            + " op=record raw=RECORDED verdict=DIAGNOSTIC");
                    }

                    checkDiagnostic = checkDiagnostic + 1;
                    continue;
                }

                if (expect == 0)
                {
                    LogSkipped(checkId, metric, op, limit, "n/a", "SKIPPED_N_A");
                    checkSkipped = checkSkipped + 1;
                    continue;
                }

                float value = 0f;
                int metricStatus = TryReadMetric(metric, out value);
                if (metricStatus != MetricOk)
                {
                    // Two different things, and the ALERT has to say which: "the driver has no
                    // branch for this name" (reason=unknownMetric, a data typo) versus "the name
                    // is real but this run cannot answer it yet" (reason names the missing
                    // sample, link or baked model). The first is a defect in the criteria file,
                    // the second is a setup gap, and collapsing them into one token made the
                    // 0.30 size_accuracy skip look like a typo.
                    Debug.Log("[ptf] ALERT cell=" + cell.GetCellId()
                        + " D=" + Fixed(presetDiametersM[diameterIndex], 3)
                        + " check=" + checkId + " metric=" + metric
                        + " reason=" + MetricReason(metricStatus) + " value=n/a verdict=SKIPPED");
                    checkSkipped = checkSkipped + 1;
                    continue;
                }

                bool raw = Compare(value, op, limit);
                bool asExpected = (expect == 1) == raw;
                string rawText = raw ? "PASS" : "FAIL";
                string verdict;
                if (asExpected)
                {
                    verdict = "EXPECTED";
                    checkPass = checkPass + 1;
                }
                else
                {
                    verdict = expect == 1 ? "UNEXPECTED_FAIL" : "UNEXPECTED_PASS";
                    checkUnexpected = checkUnexpected + 1;
                }

                LogCheck(checkId, metric, value, op, limit, expectText, rawText, verdict);
                if (!asExpected)
                {
                    Debug.Log("[ptf] ALERT cell=" + cell.GetCellId()
                        + " D=" + Fixed(presetDiametersM[diameterIndex], 3)
                        + " check=" + checkId + " metric=" + metric
                        + " value=" + Signed(value, 4)
                        + " expect=" + expectText + " verdict=" + verdict);
                }
            }

            if (notApplicableList.Length > 0)
            {
                Log("[ptf] notApplicable cell=" + cell.GetCellId()
                    + " D=" + Fixed(presetDiametersM[diameterIndex], 3)
                    + " checks=" + notApplicableList
                    + " reason=scene_has_no_scenario_at_this_diameter");
            }

            string verdictText = checkUnexpected == 0 ? "EXPECTED" : "UNEXPECTED";
            lastSummaryLine = "[ptf] summary cell=" + cell.GetCellId()
                + " D=" + Fixed(presetDiametersM[diameterIndex], 3)
                + " checks=" + total.ToString()
                + " pass=" + checkPass.ToString()
                + " unexpected=" + checkUnexpected.ToString()
                + " skipped=" + checkSkipped.ToString()
                + " diag=" + checkDiagnostic.ToString()
                + " verdict=" + verdictText
                + " elapsed=" + Fixed(driveClock, 2) + "s";
            Debug.Log(lastSummaryLine);
        }

        /// <summary>
        /// Reads one metric from the motor. Returns <see cref="MetricOk"/> or the reason it could
        /// not: <see cref="MetricUnknown"/> means the criterion names a metric the driver has no
        /// branch for (a criteria-file typo); every other code means the metric is real but this
        /// run cannot answer it yet.
        /// </summary>
        private int TryReadMetric(string metric, out float value)
        {
            value = 0f;
            if (metric == null) return MetricUnknown;

            float radius = motor.GetCollisionRadiusWorldM();
            Vector3 center = motor.GetCenterWorld();

            if (Same(metric, "support_gap_m"))
            {
                value = (center.y - radius) - motor.GetSupportSurfaceHeightWorldM();
                return MetricOk;
            }

            if (Same(metric, "support_kind"))
            {
                // -1 none, 0 ground, 1 static box top, 2 ramp (P3, never returned yet),
                // 3 support prop top. A prop top has surface id 100+i, so a check on the
                // kind must read this getter rather than test the id's sign.
                value = (float)motor.GetSupportKind();
                return MetricOk;
            }

            if (Same(metric, "support_prop_count"))
            {
                // Support prop solids the host registered with the motor (0..12).
                value = pickupHost == null ? 0f : (float)pickupHost.GetSupportPropCount();
                return MetricOk;
            }

            if (Same(metric, "deck_gap_m"))
            {
                if (!hasDeckSample)
                {
                    return MetricNoDeckSample;
                }

                value = minDeckGap;
                return MetricOk;
            }

            if (Same(metric, "lift_over_nominal_m"))
            {
                if (!hasLiftSample)
                {
                    return MetricNoLiftSample;
                }

                value = maxLiftOverNominal;
                return MetricOk;
            }

            if (Same(metric, "lift_over_nominal_in_deck_m"))
            {
                // The v2 plate criterion (CONTACT_MODEL_V2 7.7.4): the same lift, but only while
                // the ball was over the deck. No sample means the ball never got there, which is a
                // setup/geometry answer, not a physics verdict.
                if (!hasLiftInDeckSample)
                {
                    return MetricNoGateSample;
                }

                value = maxLiftInDeckOverNominal;
                return MetricOk;
            }

            if (Same(metric, "static_obb_count"))
            {
                // The v2 substitute for ramp_count (design 7.7.2): a v2 arena bakes a role=ramp
                // into a static OBB, so "the ramp is registered" is "the static table is not
                // empty". Read the motor's getter directly; a diagnostic reader must copy the
                // Udon heap first, the driver already runs inside it.
                value = (float)motor.GetStaticObbCount();
                return MetricOk;
            }

            if (Same(metric, "progress_m"))
            {
                value = ProgressMeters();
                return MetricOk;
            }

            if (Same(metric, "blocked_gap_m"))
            {
                PhysicsTestCell cell = cells[cellIndex];
                value = cell.GetApproachM() - ProgressMeters() - radius;
                return MetricOk;
            }

            if (Same(metric, "speed_ratio"))
            {
                if (peakSpeedEarly <= 0.0001f)
                {
                    value = 0f;
                    return MetricOk;
                }

                value = peakSpeedLate / peakSpeedEarly;
                return MetricOk;
            }

            if (Same(metric, "blocking_max"))
            {
                value = (float)maxBlocking;
                return MetricOk;
            }

            if (Same(metric, "saturated_delta"))
            {
                // The contact solver has no fixed sweep buffer that could saturate.
                value = 0f;
                return MetricOk;
            }

            if (Same(metric, "overflow_delta"))
            {
                value = (float)(motor.GetOverflowCount() - baseOverflow);
                return MetricOk;
            }

            if (Same(metric, "absorbed_delta"))
            {
                value = (float)(motor.GetAbsorbedCount() - baseAbsorbed);
                return MetricOk;
            }

            if (Same(metric, "attached_delta"))
            {
                int attached = 0;
                if (pickupHost != null) attached = pickupHost.GetAttachedCount();
                value = (float)(attached - baseAttached);
                return MetricOk;
            }

            if (Same(metric, "diameter_error_m"))
            {
                value = motor.GetSemanticDiameterWorldM() - presetDiametersM[diameterIndex];
                return MetricOk;
            }

            // size_accuracy is read through `diameter_error_m` (abs<= 1 mm) since steps C+D:
            // the growth quantum is 1 mm^3 (a nanometre of diameter at the start ball), so the
            // old "error in whole volume units" ratio measured the retired unit, not the
            // setter. The exact-diameter comparison is the honest one now.

            if (Same(metric, "max_center_y_m"))
            {
                value = maxCenterY;
                return MetricOk;
            }

            if (Same(metric, "grounded_frame_dy_m"))
            {
                value = maxGroundedFrameDy;
                return MetricOk;
            }

            if (Same(metric, "ramp_support_seen"))
            {
                value = (float)sawRampSupport;
                return MetricOk;
            }

            if (Same(metric, "ramp_entry_progress_m"))
            {
                // The LATCHED maximum, not the final position: a run can end far past the ramp
                // legitimately, and at D=2.2 on C13 it ends away from it after the fall, which made
                // the old final-position reading report the resting spot instead of the climb.
                value = maxRampEntryM;
                return MetricOk;
            }

            if (Same(metric, "slip_back_m"))
            {
                value = slipBack;
                return MetricOk;
            }

            if (Same(metric, "landing_gap_m"))
            {
                value = Mathf.Abs(center.y - radius);
                return MetricOk;
            }

            if (Same(metric, "fall_frames"))
            {
                value = (float)fallFrameCount;
                return MetricOk;
            }

            if (Same(metric, "max_bottom_rise_m"))
            {
                value = maxCenterY - radius;
                return MetricOk;
            }

            if (Same(metric, "ramp_count"))
            {
                value = (float)rampCountAtSwitch;
                return MetricOk;
            }

            if (Same(metric, "ramp_refusal_reason_len"))
            {
                value = (float)rampProbeReasonLength;
                return MetricOk;
            }

            if (Same(metric, "ramp_refusal_slope_deg"))
            {
                value = rampProbeSlopeDegrees;
                return MetricOk;
            }

            if (Same(metric, "frame_dy_m"))
            {
                value = maxFrameDy;
                return MetricOk;
            }

            if (Same(metric, "stuck_seconds"))
            {
                value = motor.GetStuckSeconds();
                return MetricOk;
            }

            // ---- climb readings (record-only criteria of the wall + ceiling cell) ----

            if (Same(metric, "climb_active_at_release"))
            {
                value = (float)climbActiveAtRelease;
                return hasReleaseMark ? MetricOk : MetricNoGateSample;
            }

            if (Same(metric, "cap_lock_at_release"))
            {
                value = (float)capLockAtRelease;
                return hasReleaseMark ? MetricOk : MetricNoGateSample;
            }

            if (Same(metric, "climb_active_s"))
            {
                value = climbActiveSeconds;
                return MetricOk;
            }

            if (Same(metric, "climb_lock_s"))
            {
                value = climbLockSeconds;
                return MetricOk;
            }

            if (Same(metric, "climb_height_max_m"))
            {
                value = maxClimbHeightM;
                return MetricOk;
            }

            if (Same(metric, "push_actual_speed_mps"))
            {
                if (pushTailTime <= 0f) return MetricNoGateSample;
                value = pushTailPath / pushTailTime;
                return MetricOk;
            }

            if (Same(metric, "released_descent_m"))
            {
                // Centre drop between the release and the start of the reverse pull (or the end of
                // the run when the plan has no reverse): >0 = the ball came down by itself.
                if (!hasReleaseMark) return MetricNoGateSample;
                float after = hasReverseMark ? reverseStartCenterY : center.y;
                value = releaseCenterY - after;
                return MetricOk;
            }

            if (Same(metric, "reverse_retreat_m"))
            {
                // How far back along the heading the reverse pull took the ball: >0 = it left.
                if (!hasReverseMark) return MetricNoGateSample;
                value = reverseStartProgress - ProgressMeters();
                return MetricOk;
            }

            if (Same(metric, "recovery_delta"))
            {
                // Soft or hard stuck recoveries since the run started (a latch, so a baseline
                // difference): a climb that ends in a recovery was stuck, not merely slow.
                value = (float)(motor.GetRecoveryCount() - baseRecovery);
                return MetricOk;
            }

            if (Same(metric, "final_bottom_rise_m"))
            {
                value = center.y - radius;
                return MetricOk;
            }

            // ---- slope readings ------------------------------------------------------

            if (Same(metric, "flat_drift_m"))
            {
                value = flatDriftMax;
                return MetricOk;
            }

            if (Same(metric, "slide_delay_s"))
            {
                // Rest start to the first downhill speed above SlideSpeedMps; 99 = it never slid.
                if (!hasRestMark) return MetricNoGateSample;
                value = hasSlideMark ? slideMarkSeconds - restMarkSeconds : 99f;
                return MetricOk;
            }

            if (Same(metric, "slide_peak_speed_mps"))
            {
                value = peakDownhillSpeed;
                return MetricOk;
            }

            if (Same(metric, "push_speed_ratio"))
            {
                // Speed while pushing on the slope over the same push on the flat: the downhill
                // gravity must not act on a pushed ball, so the ratio stays near 1.
                if (pushSpeedOnFlat <= 0.0001f || pushSpeedOnSlope <= 0f) return MetricNoGateSample;
                value = pushSpeedOnSlope / pushSpeedOnFlat;
                return MetricOk;
            }

            // No branch matched: this is the NAME being wrong, not this run being unable to
            // answer. Every "cannot answer" branch above returned its own code.
            return MetricUnknown;
        }

        /// <summary>
        /// Signed displacement along the AUTHORED heading. The live heading is deliberately not
        /// used: a stumble must not redefine what "forward" meant when the run started.
        /// </summary>
        private float ProgressMeters()
        {
            Vector3 center = motor.GetCenterWorld();
            float headingRadians = runHeadingDegrees * Mathf.Deg2Rad;
            float forwardX = Mathf.Sin(headingRadians);
            float forwardZ = Mathf.Cos(headingRadians);
            float dx = center.x - startCenterWorld.x;
            float dz = center.z - startCenterWorld.z;
            return dx * forwardX + dz * forwardZ;
        }

        private bool Compare(float value, int op, float limit)
        {
            switch (op)
            {
                case 0: return Mathf.Abs(value) <= limit;
                case 1: return value <= limit;
                case 2: return value >= limit;
                case 3: return Mathf.Abs(value - limit) <= 0.0001f;
                case 4: return Mathf.Abs(value) >= limit;
                default: return false;
            }
        }

        private string ExpectText(int expect)
        {
            if (expect == 1) return "pass";
            if (expect == 2) return "fail";
            return "n/a";
        }

        private string OpText(int op)
        {
            switch (op)
            {
                case 0: return "abs<=";
                case 1: return "<=";
                case 2: return ">=";
                case 3: return "==";
                case 4: return "abs>=";
                case 5: return "record";
                default: return "?";
            }
        }

        /// <summary>
        /// Name of a <c>TryReadMetric</c> status, for the ALERT line.
        /// The vocabulary is design section 8.3.2: one code per missed sample / link / baked input,
        /// so "the criteria file names a metric that does not exist" stays distinguishable from
        /// "the metric exists but this run cannot answer it".
        /// </summary>
        private string MetricReason(int status)
        {
            switch (status)
            {
                case MetricUnknown: return "unknownMetric";
                case MetricNoDeckSample: return "noDeckSample";
                case MetricNoLiftSample: return "noLiftSample";
                case MetricNoGateSample: return "noGateSample";
                default: return "unavailable";
            }
        }

        // ---- logging -----------------------------------------------------------

        private void LogSkipped(string checkId, string metric, int op, float limit,
            string expectText, string verdict)
        {
            Debug.Log("[ptf] check=" + checkId
                + " metric=" + metric
                + " value=n/a"
                + " op=" + OpText(op) + Fixed(limit, 4)
                + " expect=" + expectText
                + " raw=SKIPPED"
                + " verdict=" + verdict);
        }

        private void LogCheck(string checkId, string metric, float value, int op, float limit,
            string expectText, string rawText, string verdict)
        {
            Debug.Log("[ptf] check=" + checkId
                + " metric=" + metric
                + " value=" + Signed(value, 4)
                + " op=" + OpText(op) + Fixed(limit, 4)
                + " expect=" + expectText
                + " raw=" + rawText
                + " verdict=" + verdict);
        }

        private void LogTrajectory(float seconds)
        {
            Vector3 center = motor.GetCenterWorld();
            Debug.Log("[ptf] traj t=" + Fixed(seconds, 2)
                + " p=(" + Fixed(center.x, 2) + "," + Fixed(center.y, 2) + ","
                + Fixed(center.z, 2) + ")"
                + " sup=" + Fixed(motor.GetSupportSurfaceHeightWorldM(), 3)
                + " stuck=" + Fixed(motor.GetStuckSeconds(), 2));
        }

        private void LogFieldSummary()
        {
            string verdict = passUnexpected == 0 ? "EXPECTED" : "UNEXPECTED";
            float elapsed = Time.time - passStartTime;
            Debug.Log("[ptf] field cells=" + cells.Length.ToString()
                + " runs=" + passRuns.ToString()
                + " verdict=" + verdict
                + " unexpected=" + passUnexpected.ToString()
                + " skipped=" + passSkipped.ToString()
                + " elapsed=" + Fixed(elapsed, 0) + "s");
        }

        private void Log(string line)
        {
            Debug.Log(line);
        }

        private string ArenaReport(PhysicsTestCell cell)
        {
            if (cell == null) return "none";
            LevelArenaHost arena = cell.GetArena();
            if (arena == null) return "none";
            // obbs= is the evidence the ramp cells rest on (design 7.7.2): an arena bakes its ramp
            // into a static OBB, so the motor's ramp table is legitimately 0.
            int planes = sandboxFrame == null ? 0 : sandboxFrame.GetRegisteredPlaneCount();
            return "planes=" + planes.ToString()
                + "/boxes=" + arena.GetRegisteredBoxCount().ToString()
                + "/supports=" + arena.GetRegisteredSupportCount().ToString()
                + "/ramps=" + motor.GetRampCount().ToString()
                + "/obbs=" + motor.GetStaticObbCount().ToString()
                + "/rampCell=" + (cell.IsRampClimb() ? "1" : "0");
        }

        /// <summary>
        /// The compiled test-field revision the scene's cells report, so a stale cell program is
        /// visible in the first line ("" = a cell predates the override rows and would silently inherit
        /// them as "no override").
        /// </summary>
        private string CellRevisionText()
        {
            if (cells == null) return "none";
            for (int i = 0; i < cells.Length; i++)
            {
                if (cells[i] == null) continue;
                return cells[i].GetCellRevision();
            }

            return "none";
        }

        /// <summary>
        /// How many override rows the SCENE's bake carries (sum over cells), or "missing" when a
        /// cell has no override tables at all.
        /// </summary>
        /// <remarks>
        /// The criteria importer's report prints the same counter (<c>v2Rows=N</c>), so the first
        /// [ptf] line and the import log are directly comparable. The bake is scene data, not code:
        /// a build stamp alone cannot tell a cell baked before the override rows from one baked after, and
        /// that gap produced the 2026-11-16 O3a "31 unexpected" run where every override row was
        /// silently judged as its base row.
        /// </remarks>
        private string V2RowsText()
        {
            if (cells == null) return "none";
            int total = 0;
            for (int i = 0; i < cells.Length; i++)
            {
                if (cells[i] == null) continue;
                int rows = cells[i].GetCheckV2RowCount();
                if (rows < 0) return "missing";
                total = total + rows;
            }

            return total.ToString();
        }

        /// <summary>H_cap for the ball in play: k_h * D (a cell's balls carry no protruding prop).</summary>
        private float ClimbCapM()
        {
            float diameter = motor.GetSemanticDiameterWorldM();
            if (diameter < 0f) diameter = 0f;
            return ContactTuning.KHeight * diameter;
        }

        /// <summary>
        /// The v2 climb bound 1.02 * H_cap + cap_margin (CONTACT_MODEL_V2 6.1 C06 / 6.3): the
        /// design's "at most 1.02*H_cap + 0.02", with the 2% as a named float-fade allowance and
        /// the 2 cm as ContactTuning's cap margin. Derived per run, never typed per diameter.
        /// </summary>
        private float ClimbCapBoundM()
        {
            return V2CapFloatMargin * ClimbCapM() + ContactTuning.ClimbCapMarginM;
        }

        private string GateSummary(PhysicsTestCell cell)
        {
            string text = "";
            int total = cell.GetCheckCount();
            for (int i = 0; i < total; i++)
            {
                int gate = cell.GetCheckGateStage(i);
                if (gate <= 0) continue;
                if (text.Length > 0) text = text + ",";
                text = text + cell.GetCheckId(i) + "->P" + gate.ToString();
            }

            if (text.Length == 0) return "none";
            return text;
        }

        private void Blocked(string reason)
        {
            Debug.Log("[ptf] blocked run=" + runIndex + " reason=" + reason
                + " verdict=BLOCKED_UNTIL_setup");
            checkSkipped = checkSkipped + 1;
            passSkipped = passSkipped + 1;
            state = StateIdle;
            autoAdvance = false;
        }

        // ---- helpers -----------------------------------------------------------

        private void ResetRunSamples()
        {
            maxBlocking = 0;
            maxLiftOverNominal = 0f;
            hasLiftSample = false;
            maxLiftInDeckOverNominal = 0f;
            hasLiftInDeckSample = false;
            minDeckGap = 0f;
            hasDeckSample = false;
            peakSpeedEarly = 0f;
            peakSpeedLate = 0f;
            hasPrevCenter = false;
            previousFalling = true;
            previousCenterY = 0f;
            maxFrameDy = 0f;
            maxGroundedFrameDy = 0f;
            maxCenterY = 0f;
            maxRampEntryM = -1000f;
            sawRampSupport = 0;
            fallFrameCount = 0;
            lastForwardIntent = 0f;
            hasStopAnchor = false;
            hasStopped = false;
            stopAnchorProgress = 0f;
            stoppedMinProgress = 0f;
            slipBack = 0f;
            rampProbeReasonLength = 0;
            rampProbeSlopeDegrees = 0f;
            releaseSeconds = -1f;
            reverseSeconds = -1f;
            hasReleaseMark = false;
            releaseCenterY = 0f;
            climbActiveAtRelease = 0;
            capLockAtRelease = 0;
            hasReverseMark = false;
            reverseStartCenterY = 0f;
            reverseStartProgress = 0f;
            climbActiveSeconds = 0f;
            climbLockSeconds = 0f;
            maxClimbHeightM = 0f;
            pushTailPath = 0f;
            pushTailTime = 0f;
            previousCenter = Vector3.zero;
            lastLateralIntent = 0f;
            everPushed = false;
            flatDriftMax = 0f;
            hasRestMark = false;
            restMarkSeconds = 0f;
            hasSlideMark = false;
            slideMarkSeconds = 0f;
            peakDownhillSpeed = 0f;
            pushSpeedOnSlope = 0f;
            pushSpeedOnFlat = 0f;
            checkPass = 0;
            checkUnexpected = 0;
            checkSkipped = 0;
        }

        private int IndexOfCell(string cellId)
        {
            if (cells == null || cellId == null) return -1;
            for (int i = 0; i < cells.Length; i++)
            {
                if (cells[i] == null) continue;
                if (Same(cells[i].GetCellId(), cellId)) return i;
            }

            return -1;
        }

        private int IndexOfDiameter(float diameterM)
        {
            if (presetDiametersM == null) return -1;
            for (int i = 0; i < presetDiametersM.Length; i++)
            {
                float delta = presetDiametersM[i] - diameterM;
                if (delta < 0f) delta = -delta;
                if (delta <= 0.001f) return i;
            }

            return -1;
        }

        private bool Same(string left, string right)
        {
            if (left == null || right == null) return false;
            return string.CompareOrdinal(left, right) == 0;
        }

        /// <summary>
        /// Fixed-decimal text without a format string: UdonSharp's proven extern set does not include
        /// <c>float.ToString("F4")</c>, and the field's log columns have to line up across runs to be
        /// diffable. Values here are metres and counts, so int arithmetic is exact enough.
        /// </summary>
        private string Fixed(float value, int decimals)
        {
            if (!(value >= float.MinValue && value <= float.MaxValue)) return "nan";

            bool negative = value < 0f;
            if (negative) value = -value;

            int scale = 1;
            for (int i = 0; i < decimals; i++) scale = scale * 10;

            float scaled = value * (float)scale + 0.5f;
            if (scaled < 0f) scaled = 0f;
            if (scaled > 2000000000f) scaled = 2000000000f;
            int total = (int)scaled;

            int whole = total / scale;
            int fraction = total - whole * scale;
            string fractionText = fraction.ToString();
            while (fractionText.Length < decimals) fractionText = "0" + fractionText;

            // A value that rounds to zero must not print as "-0.0000": the log is diffed.
            string sign = negative && total != 0 ? "-" : "";
            if (decimals == 0) return sign + whole.ToString();
            return sign + whole.ToString() + "." + fractionText;
        }

        /// <summary>Same as <see cref="Fixed"/>, but a positive value keeps a leading "+".</summary>
        private string Signed(float value, int decimals)
        {
            if (!(value >= float.MinValue && value <= float.MaxValue)) return "nan";

            bool negative = value < 0f;
            if (negative) return Fixed(value, decimals);
            return "+" + Fixed(value, decimals);
        }
    }
}
