using UdonSharp;
using UnityEngine;
using VRCKatamari.BallMotor;
using VRCKatamari.Core;

namespace VRCKatamari.Pickup
{
    /// <summary>
    /// Drives every moving prop of a level from one <c>Update</c>: the original's 13 motion
    /// programs (<see cref="MotionMath"/>) written onto the prop transforms, plus the skeletal
    /// <c>Animator</c>s of the animated props. Design and numbers:
    /// Docs/Design/PickupAbsorb/MOTION_HOST.md.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Shape. One serialized parallel-array row per prop that moves, rides a moving parent, or
    /// carries an Animator; nothing per prop at runtime but the transform it writes. The rows
    /// are written by the level importer (<c>PlacementImporter</c>, motion part) from
    /// <c>motions[]</c> / <c>paths[]</c> / <c>props[].motion|phase|parent|inherit</c>; the host
    /// reads no JSON, no disk and no editor API. Rows are ordered parents first, so a rider is
    /// always composed from a parent already written this frame.
    /// </para>
    /// <para>
    /// Not driven (the contract with the absorb host and the stack-fall work): a prop whose
    /// transform no longer has the parent it had at <c>Start</c> (absorbed: it is parented to
    /// the ball's attachment root; the mode is <c>Away</c>); a prop that is not where this host
    /// last put it (a fall, a carry: the mode is <c>Yielded</c>, nothing is written until
    /// <see cref="ResumeRowAfterSettle"/>); a row that was suspended by <see cref="SuspendRow"/>;
    /// a rider whose parent is not driven (<c>Detached</c>). All of these are READ-ONLY checks
    /// of the transform, so neither <c>PickupFieldHost</c> nor the motor needs to know this
    /// host exists. A reset that puts an absorbed prop back under its original parent revives
    /// the row from its authored spawn pose.
    /// </para>
    /// <para>
    /// Time. Each row runs at its own tier (5/10/15/30 Hz, <c>updatesPerSecond</c>) on a fixed
    /// schedule: every due step advances the program by exactly <c>30 / rate</c> game frames
    /// (in chunks of at most 2, MotionMath section 12.0), so the result does not depend on the
    /// frame rate. Rows below 30 Hz are interpolated between steps every rendered frame; hop
    /// and whack (30 Hz) are written as stepped. Write decimation: a row further than the
    /// ball's radius + <c>nearMarginM</c> (default 6 m) only writes its transform on the frames
    /// it stepped (the pose is still the one for that frame's time, and the schedule is never
    /// thinned, so trajectories are identical); a stepped row also skips the write when its pose
    /// did not change (a resting hop). Culling: a row more than
    /// <c>wakeRadiusM</c> from the ball freezes (clock and state kept, nothing reset) once it is
    /// <c>wakeRadiusM + cullHysteresisM</c> away, and its Animator is disabled; WaitForPlayer's
    /// trigger gate (<c>rowTriggerRadiusM</c>, 2 m) is a different thing and is untouched by it.
    /// </para>
    /// <para>
    /// Skeletal animation is an ordinary <c>Animator</c> per instance (owner D-11). The host only
    /// (a) starts each at its phase, (b) sets <c>Moving</c> from a walker's own walk/pause phase
    /// and <c>Panic</c> from WaitForPlayer's gate, and (c) toggles the component with the cull.
    /// </para>
    /// </remarks>
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class MotionHost : UdonSharpBehaviour
    {
        private const int ModeActive = 0;
        private const int ModeAway = 1;
        private const int ModeSuspended = 2;
        private const int ModeYielded = 3;
        private const int ModeDetached = 4;
        private const int ModeDead = 5;

        /// <summary>The host's step is in game frames; a call never advances more than this.</summary>
        private const float MaxChunkFrames = 2f;
        private const int MaxCatchUpStepsPerFrame = 3;
        private const int StatsWindowCount = 4;

        // --- Wiring -------------------------------------------------------------

        [Header("Observer (the ball)")]
        [Tooltip("The ball motor: its centre is the observer for culling and WaitForPlayer. Empty = nothing is culled and no trigger fires.")]
        [SerializeField] private KatamariBallMotor motor;

        [Header("Culling and ownership")]
        [Tooltip("Rows wake within this distance of the ball (PROP_BEHAVIOURS 7.2). NOT WaitForPlayer's trigger gate.")]
        [SerializeField] private float wakeRadiusM = MotionHostMath.DefaultWakeRadiusM;
        [Tooltip("A row freezes beyond wakeRadiusM + this (7.3).")]
        [SerializeField] private float cullHysteresisM = MotionHostMath.DefaultCullHysteresisM;
        [Tooltip("A prop further than this from where the host last put it belongs to somebody else (fall, reset): the host stops writing.")]
        [SerializeField] private float driftToleranceM = MotionHostMath.DefaultDriftToleranceM;
        [Tooltip("Each row is re-tested against the ball (cull, near/far, still-parented) once per this many frames: 1/stride of the rows every frame.")]
        [SerializeField] private int classifyStrideFrames = MotionHostMath.DefaultClassifyStrideFrames;

        [Header("Write decimation (cost of far rows)")]
        [Tooltip("Rows beyond ball radius + nearMarginM write their transform only when their program stepped. Off = every row as before (for A/B in the Profiler).")]
        [SerializeField] private bool decimateFarWrites = true;
        [Tooltip("Must cover everything that reads a mover's transform: absorb reach 1.6 m, blocking margin 0.8 m + 0.25 m stickiness + the prop's half diagonal, and the ball's travel between two re-tests of a row (stride frames x speed). 6 m leaves ~2 m spare at 30 fps and 10 m/s.")]
        [SerializeField] private float nearMarginM = MotionHostMath.DefaultNearMarginM;
        [Tooltip("An Update gap longer than this (level parked and reopened) re-anchors every schedule at now; the programs continue from their frozen state.")]
        [SerializeField] private float resumeGapSeconds = MotionHostMath.DefaultResumeGapSeconds;

        [Header("Diagnostics")]
        [SerializeField] private bool logStatsEachSecond;

        // --- Rows (written by the importer; one entry per row in every column) ---

        [Header("Rows (written by the level importer)")]
        [SerializeField] private Transform[] rowTransforms;
        [SerializeField] private string[] rowItemIds;
        [Tooltip("Row index of the parent that carries this row, -1 for none. Always less than the row's own index.")]
        [SerializeField] private int[] rowParentRow;
        [Tooltip("0 none, 1 position, 2 full (props[].inherit).")]
        [SerializeField] private int[] rowInherit;
        [Tooltip("MotionHostMath class code; 0 = no own program (rider or animation-only row).")]
        [SerializeField] private int[] rowMotionClass;
        [Tooltip("D-12 seed: hash32(level.id/itemId), or the seed of the author's props[].phase.")]
        [SerializeField] private int[] rowPhaseSeed;
        [SerializeField] private float[] rowUpdatesPerSecond;
        [SerializeField] private float[] rowHalfHeightM;
        [SerializeField] private float[] rowSpeedMps;
        [SerializeField] private int[] rowSizeGroup;
        [Tooltip("WaitForPlayer's gate (motions[].params.triggerRadiusM or the size group's default).")]
        [SerializeField] private float[] rowTriggerRadiusM;
        [Tooltip("WaitForPlayer's original motionID (0x00 distance gate, 0x04 never fires until measured).")]
        [SerializeField] private int[] rowMotionIdCode;
        [SerializeField] private bool[] rowLoop;
        [SerializeField] private bool[] rowSwayTranslation;
        [Tooltip("Index into the path columns, -1 for none.")]
        [SerializeField] private int[] rowPathIndex;
        [Tooltip("The skeletal Animator of the row, or empty.")]
        [SerializeField] private Animator[] rowAnimators;

        [Header("Paths (written by the level importer)")]
        [SerializeField] private int[] pathPointStart;
        [SerializeField] private int[] pathPointCount;
        [SerializeField] private bool[] pathClosed;
        [Tooltip("Flat x,y,z world metres, all paths back to back.")]
        [SerializeField] private float[] pathPoints;

        // --- Runtime state (never serialized) -----------------------------------

        private bool ready;
        private int rowCount;
        private int frameIndex;
        private int classifyCursor;
        private float lastUpdateTime;
        private float driftToleranceSq;
        // Per-frame observer, shared by the row calls (fields, not arguments, to save copies).
        private float frameNow;
        private bool frameHaveObserver;
        private Vector3 frameObserver;
        private float[][] rowState;
        private float[][] pathArrays;
        private float[][] pathTables;
        private float[][] pathTablesOpen;
        private float[][] rowPathTable;
        private bool[] rowIsPath;
        private bool[] rowFar;
        private bool[] rowRiding;
        private bool[] rowKeepsSpawnRotation;
        private bool[] rowHasLast;
        private int[] rowSpace;
        private int[] rowWriteFrame;
        private float[] rowHopSpeedCmPerFrame;
        private Vector3[] rowLastOffset;
        private Vector3[] rowLastEuler;
        private Vector3[] spawnPos;
        private Quaternion[] spawnRot;
        private Quaternion[] spawnRotInverse;
        private Transform[] homeParent;
        private int[] rowMode;
        private bool[] rowCulled;
        private bool[] rowWrote;
        private bool[] rowTriggered;
        private bool[] rowDrivesPose;
        private bool[] rowWritesPose;
        private bool[] rowSmooth;
        private bool[] rowMoving;
        private float[] rowInterval;
        private float[] rowTierFrames;
        private float[] rowNextDue;
        private float[] rowStepTime;
        private Vector3[] prevPos;
        private Vector3[] nextPos;
        private Quaternion[] prevRot;
        private Quaternion[] nextRot;
        private Vector3[] curPos;
        private Quaternion[] curRot;

        // Diagnostics: steps and writes in the current one-second window.
        private float statsWindowStart;
        private int statsSteps;
        private int statsWrites;
        private int lastStatsSteps;
        private int lastStatsWrites;

        // --- Lifecycle ----------------------------------------------------------

        public void Start()
        {
            ready = false;
            if (!ValidateRows()) return;

            rowCount = rowTransforms.Length;
            AllocateRuntime();
            BuildPaths();

            float now = Time.time;
            driftToleranceSq = MotionHostMath.DriftToleranceSquared(driftToleranceM);
            for (int i = 0; i < rowCount; i++)
            {
                // A parent must come first: composing a rider from a row not yet written this
                // frame would lag it by one frame, so a bad order is refused, not tolerated.
                int parentRow = rowParentRow[i];
                if (parentRow >= i || parentRow < -1)
                {
                    Debug.LogWarning("[MotionHost] row " + i + " (" + rowItemIds[i] + ") has parent row "
                        + parentRow + " which does not precede it; the parent link is ignored");
                    rowParentRow[i] = -1;
                }
            }
            for (int i = 0; i < rowCount; i++)
            {
                Transform t = rowTransforms[i];
                rowMode[i] = ModeActive;
                if (t == null)
                {
                    rowMode[i] = ModeDead;
                    continue;
                }
                homeParent[i] = t.parent;
                spawnPos[i] = t.position;
                spawnRot[i] = t.rotation;
                spawnRotInverse[i] = MotionHostMath.Conjugate(t.rotation);
                curPos[i] = spawnPos[i];
                curRot[i] = spawnRot[i];
                ConfigureRowSchedule(i);
            }
            for (int i = 0; i < rowCount; i++)
            {
                ResetRowState(i, now);
                StartAnimator(i);
            }

            statsWindowStart = now;
            lastUpdateTime = now;
            ready = true;
            Debug.Log("[MotionHost] rows=" + rowCount + " paths=" + (pathPointStart == null ? 0 : pathPointStart.Length)
                + " wakeRadiusM=" + wakeRadiusM + " observer=" + (motor != null));
        }

        public void Update()
        {
            if (!ready) return;

            float now = Time.time;
            bool haveObserver = false;
            Vector3 observer = Vector3.zero;
            // float.MaxValue: nothing is far (no observer, or decimation off).
            float nearRadiusSq = float.MaxValue;
            if (motor != null)
            {
                observer = motor.GetCenterWorld();
                haveObserver = KatamariMath.IsFiniteVector(observer);
                if (haveObserver && decimateFarWrites)
                {
                    float nearRadius = MotionHostMath.NearRadiusM(motor.GetCollisionRadiusWorldM(),
                        nearMarginM);
                    nearRadiusSq = nearRadius * nearRadius;
                }
            }

            frameNow = now;
            frameHaveObserver = haveObserver;
            frameObserver = observer;
            if (MotionHostMath.IsResumeGap(lastUpdateTime, now, resumeGapSeconds)) ResyncSchedules(now);
            lastUpdateTime = now;

            frameIndex = frameIndex + 1;
            ClassifyBatch(nearRadiusSq);
            for (int i = 0; i < rowCount; i++)
            {
                DriveRow(i);
            }

            if (logStatsEachSecond && now - statsWindowStart >= 1f)
            {
                lastStatsSteps = statsSteps;
                lastStatsWrites = statsWrites;
                Debug.Log("[MotionHost] last second: steps=" + statsSteps + " writes=" + statsWrites
                    + " active=" + CountMode(ModeActive) + " away=" + CountMode(ModeAway)
                    + " yielded=" + CountMode(ModeYielded) + " culled=" + CountCulled()
                    + " far=" + CountFar());
                statsWindowStart = now;
                statsSteps = 0;
                statsWrites = 0;
            }
        }

        // --- Public surface (read-only, plus the two ownership calls) ------------

        /// <summary>Rows the host owns (0 until <c>Start</c> succeeded).</summary>
        public int GetRowCount()
        {
            return ready ? rowCount : 0;
        }

        /// <summary>The row of a prop transform, or -1. Linear; for rare calls, not per frame.</summary>
        public int FindRow(Transform prop)
        {
            if (!ready || prop == null) return -1;
            for (int i = 0; i < rowCount; i++)
            {
                if (rowTransforms[i] == prop) return i;
            }
            return -1;
        }

        /// <summary>Rows currently being driven (not away, suspended, yielded, detached or dead).</summary>
        public int GetActiveCount()
        {
            return ready ? CountMode(ModeActive) : 0;
        }

        /// <summary>Rows the host stopped driving because the prop was absorbed or hidden.</summary>
        public int GetAwayCount()
        {
            return ready ? CountMode(ModeAway) : 0;
        }

        /// <summary>Rows that stopped because somebody else moved the prop.</summary>
        public int GetYieldedCount()
        {
            return ready ? CountMode(ModeYielded) : 0;
        }

        /// <summary>Rows frozen by distance culling right now.</summary>
        public int GetCulledCount()
        {
            return ready ? CountCulled() : 0;
        }

        /// <summary>Rows beyond the near radius right now (they write only on their own steps).</summary>
        public int GetFarCount()
        {
            return ready ? CountFar() : 0;
        }

        /// <summary>Rows whose WaitForPlayer gate has fired.</summary>
        public int GetTriggeredCount()
        {
            if (!ready) return 0;
            int count = 0;
            for (int i = 0; i < rowCount; i++)
            {
                if (rowTriggered[i]) count = count + 1;
            }
            return count;
        }

        /// <summary>Program steps executed in the last completed one-second window (stats on).</summary>
        public int GetLastStepsPerSecond()
        {
            return lastStatsSteps;
        }

        /// <summary>Transform writes in the last completed one-second window (stats on).</summary>
        public int GetLastWritesPerSecond()
        {
            return lastStatsWrites;
        }

        /// <summary>True when the row's WaitForPlayer gate has fired (latched until reset).</summary>
        public bool IsRowTriggered(int row)
        {
            if (!ready || row < 0 || row >= rowCount) return false;
            return rowTriggered[row];
        }

        /// <summary>
        /// Stops driving a row until <see cref="ResumeRowAfterSettle"/>. The one explicit call a
        /// system that moves a prop on its own (the stack fall) may make; it is optional,
        /// because a moved prop is also noticed by the drift test.
        /// </summary>
        public void SuspendRow(int row)
        {
            if (!ready || row < 0 || row >= rowCount) return;
            if (rowMode[row] == ModeActive) rowMode[row] = ModeSuspended;
        }

        /// <summary>
        /// Resumes a suspended or yielded row from where the prop stands now (a settled fall,
        /// D-14): the spawn pose is rebased onto the current pose, the parent link is cut (the
        /// parent was eaten or left behind), and the program's clock continues. Path classes
        /// keep their world path.
        /// </summary>
        public void ResumeRowAfterSettle(int row)
        {
            if (!ready || row < 0 || row >= rowCount) return;
            if (rowMode[row] == ModeAway || rowMode[row] == ModeDead) return;
            Transform t = rowTransforms[row];
            if (t == null) return;

            Vector3 position = t.position;
            Quaternion rotation = t.rotation;
            spawnPos[row] = position;
            spawnRot[row] = rotation;
            spawnRotInverse[row] = MotionHostMath.Conjugate(rotation);
            rowParentRow[row] = -1;
            rowInherit[row] = MotionHostMath.InheritNone;
            rowRiding[row] = false;
            rowHasLast[row] = false;
            prevPos[row] = position;
            nextPos[row] = position;
            prevRot[row] = rotation;
            nextRot[row] = rotation;
            curPos[row] = position;
            curRot[row] = rotation;
            rowWrote[row] = false;
            float now = Time.time;
            rowNextDue[row] = now;
            rowStepTime[row] = now;
            rowMode[row] = ModeActive;
        }

        /// <summary>
        /// Round restart: every row goes back to its authored spawn pose and its seed. Rows
        /// that are away are revived by the absorb host's own reset when their transform
        /// returns to its original parent.
        /// </summary>
        public void ResetAll()
        {
            if (!ready) return;
            float now = Time.time;
            for (int i = 0; i < rowCount; i++)
            {
                if (rowMode[i] == ModeAway || rowMode[i] == ModeDead) continue;
                ReviveRow(i, now);
            }
        }

        // --- Per-row drive ------------------------------------------------------

        // Once per stride-th of the rows per frame: still ours? still awake? near or far?
        private void ClassifyBatch(float nearRadiusSq)
        {
            int batch = MotionHostMath.ClassifyBatchSize(rowCount, classifyStrideFrames);
            int index = classifyCursor;
            for (int k = 0; k < batch; k++)
            {
                ClassifyRow(index, nearRadiusSq);
                index = index + 1;
                if (index >= rowCount) index = 0;
            }
            classifyCursor = index;
        }

        private void ClassifyRow(int i, float nearRadiusSq)
        {
            if (rowMode[i] != ModeActive) return;
            Transform t = rowTransforms[i];
            if (t == null)
            {
                rowMode[i] = ModeDead;
                return;
            }
            // Taken by the absorb host (parented to the ball): stop driving it. (A write also
            // re-checks, so a prop is never written after it was taken.)
            if (t.parent != homeParent[i])
            {
                rowMode[i] = ModeAway;
                return;
            }
            if (!frameHaveObserver) return;

            Vector3 delta = curPos[i] - frameObserver;
            float distanceSquared = delta.sqrMagnitude;
            rowFar[i] = MotionHostMath.UpdateFar(rowFar[i], distanceSquared, nearRadiusSq);
            bool wasCulled = rowCulled[i];
            bool culled = MotionHostMath.UpdateCulled(wasCulled, distanceSquared, wakeRadiusM,
                cullHysteresisM);
            if (culled == wasCulled) return;
            rowCulled[i] = culled;
            Animator animator = rowAnimators != null && i < rowAnimators.Length ? rowAnimators[i] : null;
            if (animator != null) animator.enabled = !culled;
            if (!culled)
            {
                // Frozen, not reset: resume the schedule from now with the same state.
                rowNextDue[i] = frameNow;
                rowStepTime[i] = frameNow;
            }
        }

        private void DriveRow(int i)
        {
            int mode = rowMode[i];
            if (mode != ModeActive)
            {
                if (mode != ModeDead) DriveInactiveRow(i, mode);
                return;
            }
            if (rowCulled[i]) return;
            if (!rowDrivesPose[i]) return;

            bool riding = rowRiding[i];
            int parentRow = rowParentRow[i];
            if (riding && rowMode[parentRow] != ModeActive)
            {
                rowMode[i] = ModeDetached;
                return;
            }

            float now = frameNow;
            int motionClass = rowMotionClass[i];
            bool stepRan = false;
            bool poseChanged = false;
            if (motionClass != MotionHostMath.ClassNone)
            {
                float interval = rowInterval[i];
                int guard = 0;
                while (now >= rowNextDue[i] && guard < MaxCatchUpStepsPerFrame)
                {
                    guard = guard + 1;
                    stepRan = true;
                    float stepTime = rowNextDue[i];
                    rowNextDue[i] = stepTime + interval;
                    rowStepTime[i] = stepTime;
                    if (StepRow(i, motionClass)) poseChanged = true;
                }
                if (now >= rowNextDue[i]) rowNextDue[i] = MotionHostMath.NextDue(rowNextDue[i], now, interval);
            }

            // A row with no pose of its own (WaitForPlayer) writes only while riding a parent.
            if (!rowWritesPose[i]) return;
            bool smooth = rowSmooth[i];
            bool parentWrote = riding && rowWriteFrame[parentRow] == frameIndex;
            if (!MotionHostMath.ShouldWritePose(rowWrote[i], !rowFar[i], smooth,
                smooth ? stepRan : poseChanged, riding, parentWrote)) return;
            WriteRow(i, now, smooth, riding, parentRow, motionClass);
        }

        // The prop is parked (away, detached): watch for it coming back.
        private void DriveInactiveRow(int i, int mode)
        {
            if (mode == ModeAway)
            {
                // The absorb host's reset puts the prop back under its original parent.
                Transform t = rowTransforms[i];
                if (t == null) rowMode[i] = ModeDead;
                else if (t.parent == homeParent[i]) ReviveRow(i, frameNow);
                return;
            }
            if (mode == ModeDetached)
            {
                int detachedParent = rowParentRow[i];
                if (detachedParent >= 0 && rowMode[detachedParent] == ModeActive) ReviveRow(i, frameNow);
            }
        }

        // The one place a pose is written. The ownership tests sit here, in front of the write
        // they protect: a prop taken by the absorb host or moved by a fall is never overwritten.
        private void WriteRow(int i, float now, bool smooth, bool riding, int parentRow, int motionClass)
        {
            Transform t = rowTransforms[i];
            if (t == null)
            {
                rowMode[i] = ModeDead;
                return;
            }
            if (t.parent != homeParent[i])
            {
                rowMode[i] = ModeAway;
                return;
            }
            if (rowWrote[i] && MotionHostMath.HasDriftedSquared(curPos[i], t.position, driftToleranceSq))
            {
                rowMode[i] = ModeYielded;
                return;
            }

            Vector3 position = nextPos[i];
            Quaternion rotation = nextRot[i];
            if (smooth)
            {
                float fraction = MotionHostMath.SmoothFraction(now, rowStepTime[i], rowInterval[i]);
                position = Vector3.Lerp(prevPos[i], nextPos[i], fraction);
                rotation = Quaternion.Slerp(prevRot[i], nextRot[i], fraction);
            }

            if (riding && rowSpace[i] != MotionHostMath.SpaceAbsolute)
            {
                Vector3 carriedPosition;
                Quaternion carriedRotation;
                MotionHostMath.CarryByParent(rowInherit[i], curPos[parentRow], curRot[parentRow],
                    spawnPos[parentRow], spawnRotInverse[parentRow], position, rotation,
                    out carriedPosition, out carriedRotation);
                position = carriedPosition;
                rotation = carriedRotation;
            }

            t.SetPositionAndRotation(position, rotation);
            curPos[i] = position;
            curRot[i] = rotation;
            rowWrote[i] = true;
            rowWriteFrame[i] = frameIndex;
            if (logStatsEachSecond) statsWrites = statsWrites + 1;
        }

        // One due step of a row's program: tier frames in chunks of at most two (MotionMath
        // 12.0), then the standalone pose of the last chunk. True when the row's pose request
        // changed (a stepped row whose request is bit-for-bit the previous one keeps its pose).
        private bool StepRow(int i, int motionClass)
        {
            if (logStatsEachSecond) statsSteps = statsSteps + 1;
            float[] state = rowState[i];

            if (motionClass == MotionHostMath.ClassWaitForPlayer)
            {
                if (!rowTriggered[i] && frameHaveObserver)
                {
                    float distance = Vector3.Distance(frameObserver, curPos[i]);
                    if (MotionHostMath.IsTriggered(rowMotionIdCode[i], distance, rowTriggerRadiusM[i],
                        rowSizeGroup[i]))
                    {
                        rowTriggered[i] = true;
                        SetAnimatorBool(i, "Panic", true);
                    }
                }
                return false;
            }

            float remaining = rowTierFrames[i];
            Vector3 offsetOrPosition = Vector3.zero;
            Vector3 euler = Vector3.zero;
            bool ok = false;
            if (rowIsPath[i])
            {
                float[] table = rowPathTable[i];
                float[] points = pathArrays[rowPathIndex[i]];
                while (remaining > 0.0001f)
                {
                    float chunk = remaining > MaxChunkFrames ? MaxChunkFrames : remaining;
                    remaining = remaining - chunk;
                    ok = MotionHostMath.TryStepPreparedPath(motionClass, state, chunk, spawnPos[i],
                        points, table, rowSpeedMps[i], rowHalfHeightM[i], out offsetOrPosition,
                        out euler);
                    if (!ok) break;
                }
            }
            else
            {
                while (remaining > 0.0001f)
                {
                    float chunk = remaining > MaxChunkFrames ? MaxChunkFrames : remaining;
                    remaining = remaining - chunk;
                    ok = MotionHostMath.TryStep(motionClass, state, chunk, rowPhaseSeed[i],
                        rowHalfHeightM[i], rowSwayTranslation[i], rowLoop[i], rowSpeedMps[i],
                        rowHopSpeedCmPerFrame[i], null, 0, false, spawnPos[i],
                        out offsetOrPosition, out euler);
                    if (!ok) break;
                }
            }
            if (!ok) return false;

            if (motionClass == MotionHostMath.ClassSporadicWalk) UpdateMovingFlag(i, state);

            bool smooth = rowSmooth[i];
            if (!smooth && rowHasLast[i]
                && MotionHostMath.SamePoseRequest(offsetOrPosition, euler, rowLastOffset[i], rowLastEuler[i]))
            {
                return false;
            }

            Vector3 standalonePosition;
            Quaternion standaloneRotation;
            MotionHostMath.StandalonePoseIn(rowSpace[i], rowKeepsSpawnRotation[i], spawnPos[i],
                spawnRot[i], offsetOrPosition, euler, out standalonePosition, out standaloneRotation);
            if (!KatamariMath.IsFiniteVector(standalonePosition)) return false;

            if (!smooth)
            {
                rowLastOffset[i] = offsetOrPosition;
                rowLastEuler[i] = euler;
                rowHasLast[i] = true;
            }
            prevPos[i] = nextPos[i];
            prevRot[i] = nextRot[i];
            nextPos[i] = standalonePosition;
            nextRot[i] = standaloneRotation;
            return true;
        }

        // A walker is "moving" while its own state machine is in the Walk phase (state[1] == 0).
        private void UpdateMovingFlag(int i, float[] state)
        {
            bool moving = state[1] < 0.5f;
            if (moving == rowMoving[i]) return;
            rowMoving[i] = moving;
            SetAnimatorBool(i, "Moving", moving);
        }

        // The level was parked and is back: every active row restarts its schedule from now
        // (staggered like a fresh start), keeping its program state.
        private void ResyncSchedules(float now)
        {
            for (int i = 0; i < rowCount; i++)
            {
                if (rowMode[i] != ModeActive) continue;
                float stagger = rowInterval[i] > 0f ? rowInterval[i] * ((i % 8) / 8f) : 0f;
                rowNextDue[i] = now + stagger;
                rowStepTime[i] = now;
            }
        }

        // --- Setup helpers ------------------------------------------------------

        private bool ValidateRows()
        {
            if (rowTransforms == null || rowTransforms.Length == 0)
            {
                Debug.Log("[MotionHost] no rows: nothing to drive");
                return false;
            }
            int n = rowTransforms.Length;
            if (!SameLength(rowItemIds, n) || !SameLength(rowParentRow, n) || !SameLength(rowInherit, n)
                || !SameLength(rowMotionClass, n) || !SameLength(rowPhaseSeed, n)
                || !SameLength(rowUpdatesPerSecond, n) || !SameLength(rowHalfHeightM, n)
                || !SameLength(rowSpeedMps, n) || !SameLength(rowSizeGroup, n)
                || !SameLength(rowTriggerRadiusM, n) || !SameLength(rowMotionIdCode, n)
                || !SameLength(rowLoop, n) || !SameLength(rowSwayTranslation, n)
                || !SameLength(rowPathIndex, n))
            {
                Debug.LogError("[MotionHost] row columns do not all have " + n
                    + " entries (re-run the level import); the host is off");
                return false;
            }
            if (rowAnimators != null && rowAnimators.Length != 0 && rowAnimators.Length != n)
            {
                Debug.LogError("[MotionHost] rowAnimators has " + rowAnimators.Length + " entries, rows " + n
                    + " (re-run the level import); the host is off");
                return false;
            }
            int paths = pathPointStart == null ? 0 : pathPointStart.Length;
            if (pathPointCount == null ? paths != 0 : pathPointCount.Length != paths)
            {
                Debug.LogError("[MotionHost] path columns disagree (re-run the level import); the host is off");
                return false;
            }
            if (pathClosed == null ? paths != 0 : pathClosed.Length != paths)
            {
                Debug.LogError("[MotionHost] pathClosed does not match the path count; the host is off");
                return false;
            }
            return true;
        }

        private bool SameLength(Object[] column, int n)
        {
            return column != null && column.Length == n;
        }

        private bool SameLength(string[] column, int n)
        {
            return column != null && column.Length == n;
        }

        private bool SameLength(int[] column, int n)
        {
            return column != null && column.Length == n;
        }

        private bool SameLength(float[] column, int n)
        {
            return column != null && column.Length == n;
        }

        private bool SameLength(bool[] column, int n)
        {
            return column != null && column.Length == n;
        }

        private void AllocateRuntime()
        {
            rowState = new float[rowCount][];
            spawnPos = new Vector3[rowCount];
            spawnRot = new Quaternion[rowCount];
            spawnRotInverse = new Quaternion[rowCount];
            homeParent = new Transform[rowCount];
            rowMode = new int[rowCount];
            rowCulled = new bool[rowCount];
            rowWrote = new bool[rowCount];
            rowTriggered = new bool[rowCount];
            rowDrivesPose = new bool[rowCount];
            rowWritesPose = new bool[rowCount];
            rowSmooth = new bool[rowCount];
            rowMoving = new bool[rowCount];
            rowIsPath = new bool[rowCount];
            rowFar = new bool[rowCount];
            rowRiding = new bool[rowCount];
            rowKeepsSpawnRotation = new bool[rowCount];
            rowHasLast = new bool[rowCount];
            rowSpace = new int[rowCount];
            rowWriteFrame = new int[rowCount];
            rowHopSpeedCmPerFrame = new float[rowCount];
            rowPathTable = new float[rowCount][];
            rowLastOffset = new Vector3[rowCount];
            rowLastEuler = new Vector3[rowCount];
            rowInterval = new float[rowCount];
            rowTierFrames = new float[rowCount];
            rowNextDue = new float[rowCount];
            rowStepTime = new float[rowCount];
            prevPos = new Vector3[rowCount];
            nextPos = new Vector3[rowCount];
            prevRot = new Quaternion[rowCount];
            nextRot = new Quaternion[rowCount];
            curPos = new Vector3[rowCount];
            curRot = new Quaternion[rowCount];
        }

        // Splits the flat serialized points into one array per path: MotionMath takes a
        // float[] that starts at the path's first point.
        private void BuildPaths()
        {
            int paths = pathPointStart == null ? 0 : pathPointStart.Length;
            pathArrays = new float[paths][];
            pathTables = new float[paths][];
            pathTablesOpen = new float[paths][];
            for (int p = 0; p < paths; p++)
            {
                int count = pathPointCount[p];
                int start = pathPointStart[p] * 3;
                float[] points = new float[count * 3];
                bool inRange = pathPoints != null && count >= 2 && start >= 0 && start + count * 3 <= pathPoints.Length;
                if (!inRange)
                {
                    Debug.LogWarning("[MotionHost] path " + p + " is outside pathPoints; rows on it do not move");
                    pathPointCount[p] = 0;
                }
                else
                {
                    for (int k = 0; k < count * 3; k++) points[k] = pathPoints[start + k];
                    BuildPathTables(p, points, count);
                }
                pathArrays[p] = points;
            }
        }

        // The per-path numbers every step used to re-measure, once (MotionMath.BuildPathTable).
        // BackAndForth ignores the closed flag, so a closed path also gets an open table.
        private void BuildPathTables(int p, float[] points, int count)
        {
            bool closed = pathClosed[p];
            float[] table = new float[MotionMath.PathTableFloats(count, closed)];
            if (!MotionMath.BuildPathTable(points, count, closed, table))
            {
                Debug.LogWarning("[MotionHost] path " + p + " has a degenerate or non-finite segment; rows on it do not move");
            }
            pathTables[p] = table;
            if (!closed)
            {
                pathTablesOpen[p] = table;
                return;
            }
            float[] open = new float[MotionMath.PathTableFloats(count, false)];
            MotionMath.BuildPathTable(points, count, false, open);
            pathTablesOpen[p] = open;
        }

        // Which rows write a pose, how often, and with what state size.
        private void ConfigureRowSchedule(int i)
        {
            int motionClass = rowMotionClass[i];
            float rate = rowUpdatesPerSecond[i];
            if (!(rate > 0f)) rate = MotionHostMath.DefaultUpdatesPerSecond(motionClass);
            rowInterval[i] = MotionHostMath.IntervalSeconds(rate);
            rowTierFrames[i] = rate > 0f ? MotionMath.GameFramesPerSecond / rate : 0f;
            rowSmooth[i] = MotionHostMath.ShouldSmooth(rate);
            rowState[i] = new float[MotionHostMath.StateFloats(motionClass)];

            bool ownsProgram = motionClass != MotionHostMath.ClassNone && motionClass != MotionHostMath.ClassDraggedAlong;
            bool riding = rowParentRow[i] >= 0 && rowInherit[i] != MotionHostMath.InheritNone;
            bool pathMissing = MotionHostMath.IsPathClass(motionClass)
                && (rowPathIndex[i] < 0 || pathPointCount == null || rowPathIndex[i] >= pathPointCount.Length);
            rowIsPath[i] = false;
            rowPathTable[i] = null;
            if (pathMissing)
            {
                Debug.LogWarning("[MotionHost] row " + i + " (" + rowItemIds[i] + ") is a path class without a usable path; it does not move");
                ownsProgram = false;
                rowMotionClass[i] = MotionHostMath.ClassNone;
            }
            else if (motionClass == MotionHostMath.ClassDraggedAlong)
            {
                rowMotionClass[i] = MotionHostMath.ClassNone;
            }
            else if (MotionHostMath.IsPathClass(motionClass))
            {
                float[] table = motionClass == MotionHostMath.ClassBackAndForth
                    ? pathTablesOpen[rowPathIndex[i]] : pathTables[rowPathIndex[i]];
                if (table == null || table[0] < 0.5f
                    || !MotionMath.PathRowInputsOk(rowSpeedMps[i], rowHalfHeightM[i], spawnPos[i]))
                {
                    Debug.LogWarning("[MotionHost] row " + i + " (" + rowItemIds[i] + ") has an unusable path or path inputs; it does not move");
                    ownsProgram = false;
                    rowMotionClass[i] = MotionHostMath.ClassNone;
                }
                else
                {
                    rowIsPath[i] = true;
                    rowPathTable[i] = table;
                }
            }
            int finalClass = rowMotionClass[i];
            rowSpace[i] = MotionHostMath.SpaceOf(finalClass);
            rowKeepsSpawnRotation[i] = MotionHostMath.KeepsSpawnRotation(finalClass);
            rowHopSpeedCmPerFrame[i] = MotionMath.HopSpeedCmPerFrame(rowSizeGroup[i]);
            rowRiding[i] = riding;
            rowDrivesPose[i] = ownsProgram || riding;
            // WaitForPlayer runs a program (the gate) but has no pose to write.
            rowWritesPose[i] = (ownsProgram && motionClass != MotionHostMath.ClassWaitForPlayer) || riding;
        }

        private void ResetRowState(int i, float now)
        {
            int motionClass = rowMotionClass[i];
            if (rowState[i].Length > 0)
            {
                MotionHostMath.ResetState(motionClass, rowState[i], rowPhaseSeed[i]);
            }
            prevPos[i] = spawnPos[i];
            nextPos[i] = spawnPos[i];
            prevRot[i] = spawnRot[i];
            nextRot[i] = spawnRot[i];
            curPos[i] = spawnPos[i];
            curRot[i] = spawnRot[i];
            rowWrote[i] = false;
            rowTriggered[i] = false;
            rowMoving[i] = false;
            rowCulled[i] = false;
            rowHasLast[i] = false;
            rowWriteFrame[i] = -1;
            // Rows start their schedules staggered, so the first frame does not step all of them.
            float stagger = rowInterval[i] > 0f ? rowInterval[i] * ((i % 8) / 8f) : 0f;
            rowNextDue[i] = now + stagger;
            rowStepTime[i] = now;
        }

        private void ReviveRow(int i, float now)
        {
            Transform t = rowTransforms[i];
            if (t == null) return;
            ResetRowState(i, now);
            rowNextDue[i] = now;
            rowMode[i] = ModeActive;
            Animator animator = rowAnimators != null && i < rowAnimators.Length ? rowAnimators[i] : null;
            if (animator != null)
            {
                animator.enabled = true;
                animator.SetBool("Panic", false);
                animator.SetBool("Moving", false);
                HoldMovingForPathRow(i);
            }
            if (rowDrivesPose[i] && t.parent == homeParent[i])
            {
                t.SetPositionAndRotation(spawnPos[i], spawnRot[i]);
                curPos[i] = spawnPos[i];
                curRot[i] = spawnRot[i];
                rowWrote[i] = true;
                rowWriteFrame[i] = frameIndex;
            }
        }

        // Each Animator starts at its row's phase, so a field of crabs is not in lockstep (D-12).
        private void StartAnimator(int i)
        {
            if (rowAnimators == null || i >= rowAnimators.Length) return;
            Animator animator = rowAnimators[i];
            if (animator == null) return;
            animator.Play("idle", 0, MotionMath.Phase01(rowPhaseSeed[i]));
            HoldMovingForPathRow(i);
        }

        // A path walker (PathCollision, SlopingPath, BackAndForthNoYaw) walks for as long as its
        // row runs: the original starts it in MOVING and the path program never pauses it, so
        // the walk clip plays from the first frame. Without this the crab slid along its path
        // in the idle pose (SporadicWalk sets Moving from its own state machine instead).
        private void HoldMovingForPathRow(int i)
        {
            if (!MotionHostMath.HoldsMovingAnimation(rowMotionClass[i])) return;
            rowMoving[i] = true;
            SetAnimatorBool(i, "Moving", true);
        }

        private void SetAnimatorBool(int i, string parameter, bool value)
        {
            if (rowAnimators == null || i >= rowAnimators.Length) return;
            Animator animator = rowAnimators[i];
            if (animator == null || !animator.enabled) return;
            animator.SetBool(parameter, value);
        }

        private int CountMode(int mode)
        {
            int count = 0;
            for (int i = 0; i < rowCount; i++)
            {
                if (rowMode[i] == mode) count = count + 1;
            }
            return count;
        }

        private int CountFar()
        {
            int count = 0;
            for (int i = 0; i < rowCount; i++)
            {
                if (rowFar[i]) count = count + 1;
            }
            return count;
        }

        private int CountCulled()
        {
            int count = 0;
            for (int i = 0; i < rowCount; i++)
            {
                if (rowCulled[i]) count = count + 1;
            }
            return count;
        }
    }
}
