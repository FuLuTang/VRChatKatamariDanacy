using UdonSharp;
using UnityEngine;
using VRCKatamari.BallMotor;
using VRCKatamari.Pickup;

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
    /// re-prices the ball from zero volume, so sizing first would throw the work away). The prop
    /// reset comes last of the three, because it clears the candidate queue and the radial proxies
    /// the reset left behind.
    /// </para>
    /// <para>
    /// <b>Cells that cannot be measured are not failures.</b> A check gated on a stage this build
    /// does not have (P2/P3), a check whose readout is a console trace a human reads
    /// (<c>readout=trace</c>), and a check that needs the P4 step counters all report
    /// <c>BLOCKED_UNTIL_*</c> and are counted as skipped. Only a measured check can be
    /// <c>UNEXPECTED_*</c>.
    /// </para>
    /// <para>
    /// <b>Metrics are written in terms of the ball's collision radius and the cell's nominal
    /// dimension</b> (the plate thickness or the step rise), never in terms of a box's coordinates:
    /// that keeps one limit valid for all four diameters and keeps geometry in the placement file
    /// where it belongs. <c>deck_gap_m</c> is the minimum over the whole run of
    /// <c>|(centre.y - collisionRadius) - nominal|</c>, which reads 0 when the ball ever stood on
    /// the deck and <c>nominal</c> when it never did - exactly "did it climb".
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

        private PhysicsTestCell activeCell;

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
        private float previousCenterY;
        private float maxFrameDy;
        private float maxCenterY;

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
            if (cells != null)
            {
                for (int i = 0; i < cells.Length; i++)
                {
                    if (cells[i] == null) continue;
                    // Every arena starts OFF. LevelArenaHost registers itself in its own Update,
                    // and the motor's obstacle tables are global: four active cells would register
                    // four floors and sixteen walls into eight plane slots on frame one and latch
                    // the overflow counter for the whole session.
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

            if (activeCell != null && activeCell != cell) activeCell.ReleaseArena();
            activeCell = cell;
            cell.ApplyRegistration();

            if (kindWhitelist != null)
            {
                motor.SetAllowedKinds(kindWhitelist, kindWhitelist.Length);
            }

            // Baselines are read AFTER the switch and BEFORE anything else moves: the overflow
            // latch and the contact-saturation flag are permanent, so only a delta is meaningful.
            baseOverflow = motor.GetOverflowCount();
            baseSaturated = motor.WasContactBufferSaturated();
            baseAbsorbed = motor.GetAbsorbedCount();
            baseAttached = 0;
            if (pickupHost != null) baseAttached = pickupHost.GetAttachedCount();

            float radius = motor.GetCollisionRadiusWorldM();
            if (radius <= 0f) radius = 0.15f;
            Vector3 authored = cell.GetStartCenter();
            Vector3 pose = new Vector3(authored.x, radius + 0.02f, authored.z);
            float heading = cell.GetStartHeadingDegrees();
            runHeadingDegrees = heading;

            string roundId = "ptf-" + cell.GetCellId() + "-" + runIndex.ToString();
            int reason = -1;
            bool reset = motor.TryResetForNewRound(roundId, pose, heading, out reason);
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

            state = StateSize;
            stateClock = 0f;
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

            ballSize.SetDiameterM(target);

            if (ballSize.IsConverged())
            {
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
                motor.SetMoveAxes(0f, 0f, 0f);
                return;
            }

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

            if (hasPrevCenter)
            {
                float dy = center.y - previousCenterY;
                if (dy < 0f) dy = -dy;
                if (dy > maxFrameDy) maxFrameDy = dy;
            }

            hasPrevCenter = true;
            previousCenterY = center.y;
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
                string expectText = ExpectText(expect);

                if (gateStage == 1 && !motor.IsTerrainSupportEnabled())
                {
                    LogSkipped(checkId, metric, op, limit, expectText, "BLOCKED_UNTIL_P1");
                    checkSkipped = checkSkipped + 1;
                    continue;
                }

                if (gateStage == 2)
                {
                    LogSkipped(checkId, metric, op, limit, expectText, "BLOCKED_UNTIL_P2");
                    checkSkipped = checkSkipped + 1;
                    continue;
                }

                if (gateStage == 3)
                {
                    LogSkipped(checkId, metric, op, limit, expectText, "BLOCKED_UNTIL_P3");
                    checkSkipped = checkSkipped + 1;
                    continue;
                }

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
                bool known = TryReadMetric(metric, out value);
                if (!known)
                {
                    Debug.Log("[ptf] ALERT check=" + checkId + " metric=" + metric
                        + " error=unknownMetric verdict=SKIPPED");
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

        /// <summary>Reads one metric from the motor. False = this build has no such reading.</summary>
        private bool TryReadMetric(string metric, out float value)
        {
            value = 0f;
            if (metric == null) return false;

            float radius = motor.GetCollisionRadiusWorldM();
            Vector3 center = motor.GetCenterWorld();

            if (Same(metric, "support_gap_m"))
            {
                value = (center.y - radius) - motor.GetSupportSurfaceHeightWorldM();
                return true;
            }

            if (Same(metric, "deck_gap_m"))
            {
                if (!hasDeckSample) return false;
                value = minDeckGap;
                return true;
            }

            if (Same(metric, "lift_over_nominal_m"))
            {
                if (!hasLiftSample) return false;
                value = maxLiftOverNominal;
                return true;
            }

            if (Same(metric, "progress_m"))
            {
                value = ProgressMeters();
                return true;
            }

            if (Same(metric, "blocked_gap_m"))
            {
                PhysicsTestCell cell = cells[cellIndex];
                value = cell.GetApproachM() - ProgressMeters() - radius;
                return true;
            }

            if (Same(metric, "speed_ratio"))
            {
                if (peakSpeedEarly <= 0.0001f)
                {
                    value = 0f;
                    return true;
                }

                value = peakSpeedLate / peakSpeedEarly;
                return true;
            }

            if (Same(metric, "blocking_max"))
            {
                value = (float)maxBlocking;
                return true;
            }

            if (Same(metric, "saturated_delta"))
            {
                bool now = motor.WasContactBufferSaturated();
                if (now && !baseSaturated)
                {
                    value = 1f;
                    return true;
                }

                value = 0f;
                return true;
            }

            if (Same(metric, "overflow_delta"))
            {
                value = (float)(motor.GetOverflowCount() - baseOverflow);
                return true;
            }

            if (Same(metric, "absorbed_delta"))
            {
                value = (float)(motor.GetAbsorbedCount() - baseAbsorbed);
                return true;
            }

            if (Same(metric, "attached_delta"))
            {
                int attached = 0;
                if (pickupHost != null) attached = pickupHost.GetAttachedCount();
                value = (float)(attached - baseAttached);
                return true;
            }

            if (Same(metric, "diameter_error_m"))
            {
                value = motor.GetSemanticDiameterWorldM() - presetDiametersM[diameterIndex];
                return true;
            }

            if (Same(metric, "diameter_error_quanta"))
            {
                if (ballSize == null) return false;
                float quantum = ballSize.GetQuantumM();
                if (quantum <= 0.0000001f) return false;
                float error = motor.GetSemanticDiameterWorldM() - presetDiametersM[diameterIndex];
                if (error < 0f) error = -error;
                value = error / quantum;
                return true;
            }

            if (Same(metric, "max_center_y_m"))
            {
                value = maxCenterY;
                return true;
            }

            if (Same(metric, "frame_dy_m"))
            {
                value = maxFrameDy;
                return true;
            }

            if (Same(metric, "stuck_seconds"))
            {
                value = motor.GetStuckSeconds();
                return true;
            }

            return false;
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
                + "/supports=" + arena.GetRegisteredSupportCount().ToString();
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
            previousCenterY = 0f;
            maxFrameDy = 0f;
            maxCenterY = 0f;
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
