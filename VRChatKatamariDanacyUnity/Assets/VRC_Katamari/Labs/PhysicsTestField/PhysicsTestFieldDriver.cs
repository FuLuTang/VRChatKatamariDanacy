using UdonSharp;
using UnityEngine;
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
    /// path: switching cells is <see cref="PhysicsTestCell.ApplyRegistration"/> (which is
    /// <c>LevelArenaHost.Reapply</c>), sizing is <see cref="LabBallSizeSetter"/>, driving is
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
    /// <see cref="PickupFieldHost"/> wired or with the host's prop obstacles switched off (the
    /// only caller of <c>SetSupportProps</c>), a check whose readout is a console trace a human
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

        // A converged setter must also have actually resized the ball. A constant, not a serialized
        // field: a field added to an already-serialized UdonBehaviour reads 0 until the criteria are
        // re-imported, and a zero tolerance would block every run instead of guarding one.
        private const float SizeAppliedToleranceM = 0.02f;

        // Stamped into the first log line so "which U# program is running" is checkable. The
        // 2026-11-15 matrix ran a pre-P3 driver against post-P3 data and nobody could tell from
        // the log; the build line and the reason= vocabulary make that impossible to repeat.
        private const string DriverRevision = "2026-11-15c";

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
        private const int MetricNoSetter = 4;
        private const int MetricSizeModelMissing = 5;
        private const int MetricQuantumUnavailable = 6;

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
        private bool baseSaturated;
        private int baseAbsorbed;
        private int baseAttached;
        private int maxBlocking;
        private float maxLiftOverNominal;
        private bool hasLiftSample;
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

        // ---- per-run result ----------------------------------------------------

        private int checkPass;
        private int checkUnexpected;
        private int checkSkipped;
        private string lastSummaryLine = "";

        // ---- public API --------------------------------------------------------

        public void Start()
        {
            // Nothing here touches the motor: its Start may not have run yet, and its stores are
            // allocated there. The first Update waits for a positive diameter instead of racing.
            int cellCount = cells == null ? 0 : cells.Length;
            Debug.Log("[ptf] build=" + DriverRevision + " cells=" + cellCount.ToString()
                + " presets=" + (presetDiametersM == null ? 0 : presetDiametersM.Length).ToString());
            if (cells != null)
            {
                for (int i = 0; i < cells.Length; i++)
                {
                    if (cells[i] == null) continue;
                    if (i == 0)
                    {
                        // The first cell's arena is the only one allowed to register at load.
                        // Every cell stays visible (compact field), so this is about the motor's
                        // global tables, not about the floor: one registrant cannot overflow.
                        // It registers itself in LevelArenaHost.Update once the motor is ready,
                        // and a later switch re-applies it rather than adding a second one.
                        cells[i].ApplyRegistration();
                        continue;
                    }

                    // Every other arena starts SUSPENDED: its geometry stays visible, but its
                    // host may not self-register. LevelArenaHost registers in its own Update and
                    // the motor's tables are global, so nine registrants would put nine floors
                    // and thirty-six walls into eight plane slots on frame one and latch the
                    // overflow counter for the whole session.
                    cells[i].ReleaseArena();
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

            if (state == StateIdle) return;
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

        private void TickSwitch()
        {
            PhysicsTestCell cell = cells[cellIndex];
            if (cell == null)
            {
                Blocked("noCell");
                return;
            }

            // Exactly one arena may be REGISTERED; all of them stay visible. ReleaseArena now
            // means "suspend self-registration", not "hide", so this loop is safe to run on
            // every switch and on Start without turning the field into a set of empty pads.
            // It does not trust "the previous cell": Start leaves cell 0 allowed to register
            // without ever having run a switch, so the first switch must still suspend it.
            for (int i = 0; i < cells.Length; i++)
            {
                if (cells[i] == null) continue;
                if (cells[i] == cell) continue;
                cells[i].ReleaseArena();
            }

            cell.ApplyRegistration();

            if (kindWhitelist != null)
            {
                motor.SetAllowedKinds(kindWhitelist, kindWhitelist.Length);
            }

            // The arena has registered its own ramps (or refused them at bake time); read that
            // count before the probe so it describes what the ARENA put in the motor. Then, for
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
            baseSaturated = motor.WasContactBufferSaturated();
            baseAbsorbed = motor.GetAbsorbedCount();
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
                cell.ApplyRegistration();
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
                motor.SetMoveAxes(0f, 0f, 0f);
                return;
            }

            lastForwardIntent = forward;
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

            int total = cell.GetCheckCount();
            for (int i = 0; i < total; i++)
            {
                string checkId = cell.GetCheckId(i);
                string metric = cell.GetCheckMetric(i);
                int op = cell.GetCheckOp(i);
                float limit = cell.GetCheckLimit(i);
                int readout = cell.GetCheckReadout(i);
                int gateStage = cell.GetCheckGateStage(i);
                int expect = cell.GetCheckExpect(i, diameterIndex);

                // A deck check asks "did the ball get onto the deck"; whether it SHOULD is the
                // runtime step limit, not the preset label. The presets are quantized (the
                // 0.30 preset is 0.2997369 m, so its limit is 0.074934 m) and C09's deck is
                // 0.075 m: a baked "pass" would demand a climb the step rule forbids (design
                // section 8.3.1). Only the deck metric is derived here; every other check
                // keeps its baked row, so no other criterion's meaning moves.
                if (Same(metric, "deck_gap_m") && !cell.IsRampClimb())
                {
                    float deckRise = cell.GetNominalM();
                    float runStepLimit = motor.GetStepHeightLimitWorldM();
                    if (deckRise > 0f && runStepLimit >= 0f)
                    {
                        expect = deckRise <= runStepLimit ? 1 : 2;
                        Log("[ptf] derived cell=" + cell.GetCellId() + " check=" + checkId
                            + " deck_rise=" + Fixed(deckRise, 4)
                            + " step_limit=" + Fixed(runStepLimit, 6)
                            + " expect=" + ExpectText(expect));
                    }
                }

                string expectText = ExpectText(expect);

                if (gateStage == 1 && !motor.IsTerrainSupportEnabled())
                {
                    LogSkipped(checkId, metric, op, limit, expectText, "BLOCKED_UNTIL_P1");
                    checkSkipped = checkSkipped + 1;
                    continue;
                }

                // P2 is in the motor, but prop tops only reach it through SetSupportProps, and
                // PickupFieldHost is the only caller. Without a host - or with its prop
                // obstacles switched off (it clears both sets) - the P2 readings (what supports
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

            string verdictText = checkUnexpected == 0 ? "EXPECTED" : "UNEXPECTED";
            lastSummaryLine = "[ptf] summary cell=" + cell.GetCellId()
                + " D=" + Fixed(presetDiametersM[diameterIndex], 3)
                + " checks=" + total.ToString()
                + " pass=" + checkPass.ToString()
                + " unexpected=" + checkUnexpected.ToString()
                + " skipped=" + checkSkipped.ToString()
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
                // Prop tops the motor actually holds (0..6), not the host's candidate count.
                value = (float)motor.GetSupportPropCount();
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
                bool now = motor.WasContactBufferSaturated();
                if (now && !baseSaturated)
                {
                    value = 1f;
                    return MetricOk;
                }

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

            if (Same(metric, "diameter_error_quanta"))
            {
                if (ballSize == null)
                {
                    return MetricNoSetter;
                }

                // The quantum is a property of the motor's size model, and the setter derives it
                // analytically from the baseline the criteria importer mirrored onto it. Zero
                // means that mirror is missing (or the model is degenerate), not that this run
                // was too short - so the ALERT names the missing input instead of pretending
                // the metric does not exist.
                if (!ballSize.IsCalibrated())
                {
                    return MetricSizeModelMissing;
                }

                float quantum = ballSize.GetQuantumM();
                if (quantum <= 0.0000001f)
                {
                    return MetricQuantumUnavailable;
                }

                float error = motor.GetSemanticDiameterWorldM() - presetDiametersM[diameterIndex];
                if (error < 0f) error = -error;
                value = error / quantum;
                return MetricOk;
            }

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
                PhysicsTestCell cell = cells[cellIndex];
                value = ProgressMeters() - cell.GetRampFootM();
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
                case MetricNoSetter: return "noSetter";
                case MetricSizeModelMissing: return "sizeModelMissing";
                case MetricQuantumUnavailable: return "quantumUnavailable";
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
                + " lift=" + Fixed(motor.GetGroundProxyLiftWorldM(), 3)
                + " cap=" + Fixed(motor.GetProxyProtrusionCapWorldM(), 4)
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
            return "planes=" + arena.GetRegisteredPlaneCount().ToString()
                + "/boxes=" + arena.GetRegisteredBoxCount().ToString()
                + "/supports=" + arena.GetRegisteredSupportCount().ToString()
                + "/ramps=" + motor.GetRampCount().ToString()
                + "/rampCell=" + (cell.IsRampClimb() ? "1" : "0");
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
