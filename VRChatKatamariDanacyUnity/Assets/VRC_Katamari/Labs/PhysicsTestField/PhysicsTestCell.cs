using UdonSharp;
using UnityEngine;
using VRCKatamari.Story;

namespace VRCKatamari.Labs.PhysicsTestField
{
    /// <summary>
    /// One cell of the ball physics test field: the arena it owns, where its ball starts,
    /// the rect that decides when it is the active cell, and its criteria as parallel arrays.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The geometry is NOT in this component and not in the criteria JSON: it is
    /// <c>Docs/Design/Levels/physics_test_field/&lt;cellId&gt;/placement.json</c>, imported
    /// into <c>LevelContent/&lt;cellId&gt;</c> by <c>PlacementImporter</c>. Everything
    /// serialized here is written by <c>PhysicsTestFieldCriteriaImporter</c> from that
    /// placement plus <c>Docs/Design/BallMotor/physics_test_field.json</c>, so no number is
    /// typed twice.
    /// </para>
    /// <para>
    /// Why parallel arrays: UdonSharp has no user structs, no lists and no reflection, so one
    /// criterion can only be one row of several arrays (design section 7.3). The readouts are
    /// integers because a string switch per check per run would cost the frame the field is
    /// trying to measure.
    /// </para>
    /// <para>
    /// Every cell's arena object stays ACTIVE, deliberately: the field is meant to be seen as a
    /// whole. Registration is a separate switch: <see cref="LevelArenaHost"/> registers itself in
    /// its own <c>Update</c>, and the motor's obstacle tables are global, so the driver calls
    /// <see cref="ReleaseArena"/> on every cell at load and registers them all in one pass with
    /// <see cref="ApplySandboxRegistration"/>: the manual sandbox, every cell's boards on one big
    /// floor inside one outer wall (the frame host). A cell document carries no floor and no wall
    /// of its own (<c>level.arena.frame = "shared"</c>).
    /// </para>
    /// </remarks>
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class PhysicsTestCell : UdonSharpBehaviour
    {
        // Stamped into the driver's first log line so "which compiled test-field code is
        // running" is checkable. This component gained the deck footprint, the six per-check
        // override arrays and (2026-11-17a) the per-diameter run mask and the record-only op, and
        // a scene baked by an older program would not have them at all.
        private const string CellRevision = "2026-11-17b";

        // ---- identity and arena ------------------------------------------------

        [Header("Identity")]
        [SerializeField] private string cellId = "";

        [Tooltip("This cell's only registrant. Written by the criteria importer from " +
                 "LevelContent/<cellId>.")]
        [SerializeField] private LevelArenaHost arena;

        // ---- spatial frame (from the cell's placement) -------------------------

        [Header("Start pose (from placement level.ballStart / ballHeadingDeg)")]
        [Tooltip("Author annotation: y is the authored 0.15. The driver ignores y and " +
                 "teleports to collision radius + 0.02 so no diameter starts inside the floor.")]
        [SerializeField] private Vector3 startCenter;
        [SerializeField] private float startHeadingDegrees;

        [Header("Footprint (the cell's pad rect; from the criteria file)")]
        [SerializeField] private Vector2 footprintCenter;
        [SerializeField] private Vector2 footprintSize;

        [Header("Reference scalars (derived by the criteria importer from the placement)")]
        [Tooltip("The dimension under test: the plate thickness or the step rise, metres. " +
                 "Checks compare (centre.y - collision radius) against it, which makes the " +
                 "limit diameter-free.")]
        [SerializeField] private float nominalM;
        [Tooltip("How far the ball must travel along its heading before it reaches the " +
                 "primary box's near face, metres. blocked_gap_m = approach - progress - radius.")]
        [SerializeField] private float approachM;

        [Header("Deck footprint (from the placement's primary box, baked by the importer)")]
        [Tooltip("World XZ centre of the deck the cell measures against (the first box the " +
                 "ball's heading meets). The v2 lift criterion is sampled only here.")]
        [SerializeField] private Vector2 deckCenterXZ;
        [Tooltip("Half extents of that deck in XZ, metres. The v2 lift gate is this rect " +
                 "grown by one collision radius - no constant is typed in the driver.")]
        [SerializeField] private Vector2 deckHalfSizeM;

        // ---- ramp geometry (derived by the criteria importer) -------------------

        [Header("Ramp cell (derived from the placement's role=ramp / kind=ramp_probe)")]
        [Tooltip("True when the placement carries a role=ramp. The deck_gap expectation is " +
                 "then a climb, not a step rule comparison: a ramp exists precisely to lift " +
                 "the ball above every stepLimit.")]
        [SerializeField] private bool rampClimb;
        [Tooltip("Distance along the authored heading from the start to the ramp's footprint, " +
                 "metres. ramp_entry_progress_m = progress - this.")]
        [SerializeField] private float rampFootM;
        [Tooltip("True when the placement carries a kind=ramp_probe box. The driver asks the " +
                 "motor to register that box's +Y face as a ramp: over the slope guard the " +
                 "call must be refused, and the refusal is the criterion.")]
        [SerializeField] private bool rampProbe;
        [SerializeField] private Vector3 rampProbeCenter;
        [SerializeField] private Vector3 rampProbeNormal;
        [SerializeField] private Vector3 rampProbeRight;
        [SerializeField] private float rampProbeHalfWidth;
        [SerializeField] private float rampProbeHalfRun;

        // ---- drive timeline (from cells[].drive) -------------------------------

        [Header("Run mask")]
        [Tooltip("One bit per preset diameter, in preset order: 1 = do NOT run this cell at that " +
                 "diameter (the scene cannot host it, e.g. a ceiling lower than the ball). 0 = run " +
                 "at every diameter, which is what an older scene without the field means.")]
        [SerializeField] private int skipDiameterMask;

        [Header("Drive timeline: (t, forward, turn, lateral), applied in order")]
        [SerializeField] private float[] driveTimes;
        [SerializeField] private float[] driveForward;
        [SerializeField] private float[] driveTurn;
        [SerializeField] private float[] driveLateral;
        [Tooltip("Seconds to keep measuring after the last drive row (the ball is released).")]
        [SerializeField] private float stopSeconds = 1.5f;

        // ---- criteria (one row per check) --------------------------------------

        [Header("Criteria: parallel arrays, one row per check")]
        [SerializeField] private string[] checkIds;
        [SerializeField] private string[] checkMetrics;
        [Tooltip("0 = abs<=, 1 = <=, 2 = >=, 3 = ==, 4 = abs>=, 5 = record (a diagnostic: the " +
                 "value is logged, never judged)")]
        [SerializeField] private int[] checkOps;
        [SerializeField] private float[] checkLimits;
        [Tooltip("0 = frame sampler, 1 = trace log (manual), 2 = motor counter (P4)")]
        [SerializeField] private int[] checkReadouts;
        [Tooltip("0 = none, 1 = P1, 2 = P2, 3 = P3")]
        [SerializeField] private int[] checkGateStages;
        [Tooltip("Two bits per preset diameter, in preset order: 0 n/a, 1 pass, 2 fail")]
        [SerializeField] private int[] checkExpectMasks;
        [Tooltip("One bit per preset diameter: 1 = this criterion is measurable at that " +
                 "diameter, 0 = the scene has no such scenario (explicit SKIPPED_NOT_APPLICABLE, " +
                 "never a silent pass). Missing or short array = applicable everywhere.")]
        [SerializeField] private int[] checkAppliesMasks;

        // ---- the override rows (one row per check, empty = "same as the base row") ----
        //
        // A check may carry an override block that replaces its metric, op/limit, threshold or
        // per-diameter expectation (design section 5.6); without one the base row above is the
        // criterion.
        // The arrays are as long as checkIds but hold a sentinel per row, so "no override" and
        // "an override that changes nothing" stay distinguishable: "" / -1 mean "inherit", and a
        // 0 expect mask is a real value (n/a at every diameter).

        [Header("override rows: one row per check, sentinel = inherit the base row")]
        [Tooltip("1 = this check has an override; 0 = the base row is the criterion.")]
        [SerializeField] private int[] checkHasV2;
        [Tooltip("Replacement metric name, or empty to keep the base metric.")]
        [SerializeField] private string[] checkV2Metrics;
        [Tooltip("Replacement op code (0..4), or -1 to keep the base op AND the base limit. An op " +
                 "override always carries its own limit.")]
        [SerializeField] private int[] checkV2Ops;
        [SerializeField] private float[] checkV2Limits;
        [Tooltip("Two bits per preset diameter, in preset order: 0 n/a, 1 pass, 2 fail. " +
                 "-1 = inherit the base expect row.")]
        [SerializeField] private int[] checkV2ExpectMasks;
        [Tooltip("0 = use checkV2Limits, 1 = climb_cap_bound (the driver derives " +
                 "1.02 * H_cap + cap_margin from ContactTuning at run time).")]
        [SerializeField] private int[] checkV2Thresholds;

        // ---- operator seams ----------------------------------------------------

        /// <summary>
        /// Appends this cell's boards to what the motor already holds (the manual sandbox):
        /// <c>LevelArenaHost.Register</c> with no clear and surface ids offset by what earlier
        /// cells registered.
        /// </summary>
        public void ApplySandboxRegistration(int boxSurfaceIdOffset, int rampSurfaceIdOffset)
        {
            if (arena == null) return;
            arena.gameObject.SetActive(true);
            arena.Register(false, boxSurfaceIdOffset, rampSurfaceIdOffset);
        }

        /// <summary>Static boxes this cell's arena registered in its last registration.</summary>
        public int GetRegisteredBoxCount()
        {
            if (arena == null) return 0;
            return arena.GetRegisteredBoxCount();
        }

        /// <summary>Static ramps this cell's arena registered in its last registration.</summary>
        public int GetRegisteredRampCount()
        {
            if (arena == null) return 0;
            return arena.GetRegisteredRampCount();
        }

        /// <summary>
        /// Stops this cell's arena from registering itself while keeping its boards visible. The host's own Update is the only self-registration path, so suspending it
        /// is exactly "visible but not registered".
        /// </summary>
        public void ReleaseArena()
        {
            if (arena == null) return;
            arena.SuspendRegistration();
        }

        // ---- identity / spatial getters ---------------------------------------

        public string GetCellId()
        {
            if (cellId == null) return "";
            return cellId;
        }

        /// <summary>Build stamp of this component, for the driver's first log line.</summary>
        public string GetCellRevision()
        {
            return CellRevision;
        }

        public LevelArenaHost GetArena()
        {
            return arena;
        }

        public Vector3 GetStartCenter()
        {
            return startCenter;
        }

        public float GetStartHeadingDegrees()
        {
            return startHeadingDegrees;
        }

        public float GetNominalM()
        {
            return nominalM;
        }

        public float GetApproachM()
        {
            return approachM;
        }

        /// <summary>True when the placement's ramp is the thing that lifts the ball.</summary>
        public bool IsRampClimb()
        {
            return rampClimb;
        }

        /// <summary>Distance from the start to the ramp footprint along the heading, metres.</summary>
        public float GetRampFootM()
        {
            return rampFootM;
        }

        /// <summary>True when this cell asks the motor to judge an over-limit ramp plane.</summary>
        public bool HasRampProbe()
        {
            return rampProbe;
        }

        public Vector3 GetRampProbeCenter()
        {
            return rampProbeCenter;
        }

        public Vector3 GetRampProbeNormal()
        {
            return rampProbeNormal;
        }

        public Vector3 GetRampProbeRight()
        {
            return rampProbeRight;
        }

        public float GetRampProbeHalfWidth()
        {
            return rampProbeHalfWidth;
        }

        public float GetRampProbeHalfRun()
        {
            return rampProbeHalfRun;
        }

        public float GetStopSeconds()
        {
            return stopSeconds;
        }

        /// <summary>Rect centre in world XZ as (x, z).</summary>
        public Vector2 GetFootprintCenter()
        {
            return footprintCenter;
        }

        public Vector2 GetFootprintSize()
        {
            return footprintSize;
        }

        /// <summary>The deck's XZ centre (the primary box the ball's heading meets).</summary>
        public Vector2 GetDeckCenterXZ()
        {
            return deckCenterXZ;
        }

        /// <summary>The deck's XZ half extents, metres.</summary>
        public Vector2 GetDeckHalfSizeM()
        {
            return deckHalfSizeM;
        }

        /// <summary>
        /// True when (x, z) is inside the DECK's rect grown by <paramref name="marginM"/>.
        /// </summary>
        /// <remarks>
        /// This is the v2 lift criterion's spatial gate (CONTACT_MODEL_V2 section 7.7.4): the
        /// measured lift is only the plate's, so it is sampled while the ball is over the plate,
        /// not while it climbs the far boundary wall. Both numbers are data - the deck footprint
        /// baked by the criteria importer from the arena collider, and the caller's margin (one
        /// collision radius) - so no distance is typed in the driver.
        /// </remarks>
        public bool IsInDeckGate(float x, float z, float marginM)
        {
            return ContainsRect(deckCenterXZ, deckHalfSizeM, x, z, marginM);
        }

        /// <summary>
        /// Inside (centre, size) grown by marginM, where size is the FULL extent. The deck gate's
        /// implementation.
        /// </summary>
        private bool ContainsRect(Vector2 center, Vector2 size, float x, float z, float marginM)
        {
            float halfX = size.x * 0.5f + marginM;
            float halfZ = size.y * 0.5f + marginM;
            return x >= center.x - halfX && x <= center.x + halfX &&
                   z >= center.y - halfZ && z <= center.y + halfZ;
        }

        // ---- drive timeline getters -------------------------------------------

        public int GetDriveCount()
        {
            if (driveTimes == null) return 0;
            return driveTimes.Length;
        }

        public float GetDriveTime(int index)
        {
            if (driveTimes == null || index < 0 || index >= driveTimes.Length) return 0f;
            return driveTimes[index];
        }

        public float GetDriveForward(int index)
        {
            if (driveForward == null || index < 0 || index >= driveForward.Length) return 0f;
            return driveForward[index];
        }

        public float GetDriveTurn(int index)
        {
            if (driveTurn == null || index < 0 || index >= driveTurn.Length) return 0f;
            return driveTurn[index];
        }

        public float GetDriveLateral(int index)
        {
            if (driveLateral == null || index < 0 || index >= driveLateral.Length) return 0f;
            return driveLateral[index];
        }

        /// <summary>The last authored timestamp: drive ends there, measurement continues.</summary>
        public float GetDriveEndSeconds()
        {
            if (driveTimes == null || driveTimes.Length == 0) return 0f;
            float last = 0f;
            for (int i = 0; i < driveTimes.Length; i++)
            {
                if (driveTimes[i] > last) last = driveTimes[i];
            }
            return last;
        }

        // ---- criteria getters --------------------------------------------------

        public int GetCheckCount()
        {
            if (checkIds == null) return 0;
            return checkIds.Length;
        }

        public string GetCheckId(int index)
        {
            if (checkIds == null || index < 0 || index >= checkIds.Length) return "";
            return checkIds[index];
        }

        public string GetCheckMetric(int index)
        {
            if (checkMetrics == null || index < 0 || index >= checkMetrics.Length) return "";
            return checkMetrics[index];
        }

        public int GetCheckOp(int index)
        {
            if (checkOps == null || index < 0 || index >= checkOps.Length) return 0;
            return checkOps[index];
        }

        public float GetCheckLimit(int index)
        {
            if (checkLimits == null || index < 0 || index >= checkLimits.Length) return 0f;
            return checkLimits[index];
        }

        public int GetCheckReadout(int index)
        {
            if (checkReadouts == null || index < 0 || index >= checkReadouts.Length) return 0;
            return checkReadouts[index];
        }

        public int GetCheckGateStage(int index)
        {
            if (checkGateStages == null || index < 0 || index >= checkGateStages.Length) return 0;
            return checkGateStages[index];
        }

        public int GetCheckExpectMask(int index)
        {
            if (checkExpectMasks == null || index < 0 || index >= checkExpectMasks.Length) return 0;
            return checkExpectMasks[index];
        }

        /// <summary>Expectation for one preset index: 0 n/a, 1 pass, 2 fail.</summary>
        public int GetCheckExpect(int index, int diameterIndex)
        {
            int mask = GetCheckExpectMask(index);
            int shift = diameterIndex * 2;
            if (shift < 0 || shift > 30) return 0;
            return (mask >> shift) & 3;
        }

        public int GetCheckAppliesMask(int index)
        {
            if (checkAppliesMasks == null || index < 0 || index >= checkAppliesMasks.Length) return 0;
            return checkAppliesMasks[index];
        }

        /// <summary>
        /// Whether this criterion's SCENARIO exists at one preset diameter. A missing mask (older
        /// scene data) means "applicable everywhere", so an import that predates this field cannot
        /// silently turn every check into a skip.
        /// </summary>
        /// <remarks>
        /// 0 means the cell's geometry makes the scenario impossible at that diameter - e.g. a
        /// 45-degree ramp whose run is shorter than the ball's radius, or a landing whose deck is
        /// lower than the ball's centre. The driver reports those as
        /// <c>SKIPPED_NOT_APPLICABLE</c> and counts them as skipped; the rule and the numbers are
        /// design section 4.1.4. It is NOT a way to retire a red criterion: a check that stays
        /// applicable keeps failing honestly.
        /// </remarks>
        public bool CheckApplies(int index, int diameterIndex)
        {
            if (checkAppliesMasks == null || index < 0 || index >= checkAppliesMasks.Length) return true;
            if (diameterIndex < 0 || diameterIndex > 30) return true;
            int mask = checkAppliesMasks[index];
            if (mask == 0) return true;
            return ((mask >> diameterIndex) & 1) == 1;
        }

        /// <summary>
        /// Whether the driver runs this cell at one preset diameter. False when the cell declares
        /// that its geometry cannot host the ball (the driver then logs a skipped run instead of
        /// driving a ball that starts inside a slab).
        /// </summary>
        public bool RunsAtDiameter(int diameterIndex)
        {
            if (diameterIndex < 0 || diameterIndex > 30) return true;
            return ((skipDiameterMask >> diameterIndex) & 1) == 0;
        }

        // ---- override rows -----------------------------------------------------

        /// <summary>True when this criterion carries an override row (design section 5.6).</summary>
        /// <remarks>
        /// A false answer means the base row above is the criterion - that is the normal case.
        /// The driver consults the other getters only when this is true.
        /// </remarks>
        public bool HasCheckV2(int index)
        {
            if (index < 0 || index >= GetCheckCount()) return false;
            if (checkHasV2 == null || index >= checkHasV2.Length) return false;
            return checkHasV2[index] != 0;
        }

        /// <summary>Replacement metric name, or "" to keep the base metric.</summary>
        public string GetCheckV2Metric(int index)
        {
            if (checkV2Metrics == null || index < 0 || index >= checkV2Metrics.Length) return "";
            return checkV2Metrics[index];
        }

        /// <summary>Replacement op code (0..4), or -1 to keep the base op and limit.</summary>
        public int GetCheckV2Op(int index)
        {
            if (checkV2Ops == null || index < 0 || index >= checkV2Ops.Length) return -1;
            return checkV2Ops[index];
        }

        /// <summary>The v2 op's own limit (only read when <see cref="GetCheckV2Op"/> is not -1).</summary>
        public float GetCheckV2Limit(int index)
        {
            if (checkV2Limits == null || index < 0 || index >= checkV2Limits.Length) return 0f;
            return checkV2Limits[index];
        }

        /// <summary>v2 expectation for one preset index: 0 n/a, 1 pass, 2 fail, -1 inherit.</summary>
        public int GetCheckV2Expect(int index, int diameterIndex)
        {
            if (checkV2ExpectMasks == null || index < 0 || index >= checkV2ExpectMasks.Length) return -1;
            int mask = checkV2ExpectMasks[index];
            if (mask < 0) return -1;
            int shift = diameterIndex * 2;
            if (shift < 0 || shift > 30) return -1;
            return (mask >> shift) & 3;
        }

        /// <summary>0 = the v2 limit above, 1 = climb_cap_bound (derived by the driver).</summary>
        public int GetCheckV2Threshold(int index)
        {
            if (checkV2Thresholds == null || index < 0 || index >= checkV2Thresholds.Length) return 0;
            return checkV2Thresholds[index];
        }

        /// <summary>
        /// How many criteria of this cell carry a v2 override row, or -1 when this cell has
        /// no v2 tables at all.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The six per-check override arrays are baked by
        /// <c>PhysicsTestFieldCriteriaImporter</c>. A cell baked before they existed has none of
        /// them, so <see cref="HasCheckV2"/> answers false for every check
        /// and a run silently judges every override row as its base row - the exact
        /// 2026-11-16 regression, where a working ball produced 31 "unexpected" verdicts.
        /// </para>
        /// <para>
        /// -1 (tables absent) is deliberately different from 0 (tables present, this cell has
        /// no overrides): the driver prints the count in its first line and refuses to judge a
        /// run against a bake that predates the tables. The importer prints the same
        /// counter in its own report (<c>v2Rows=11</c>), so the two lines are comparable.
        /// </para>
        /// </remarks>
        public int GetCheckV2RowCount()
        {
            if (checkHasV2 == null || checkV2Metrics == null || checkV2Ops == null ||
                checkV2Limits == null || checkV2ExpectMasks == null || checkV2Thresholds == null)
            {
                return -1;
            }

            // The importer sizes every criteria array to checkIds, so a table shorter than the
            // check list is a partial bake, not a cell with no overrides: HasCheckV2 would answer
            // false for the tail, which is the same silent downgrade this method exists to catch.
            int rows = GetCheckCount();
            if (checkHasV2.Length < rows || checkV2Metrics.Length < rows ||
                checkV2Ops.Length < rows || checkV2Limits.Length < rows ||
                checkV2ExpectMasks.Length < rows || checkV2Thresholds.Length < rows)
            {
                return -1;
            }

            int count = 0;
            for (int i = 0; i < rows; i++)
            {
                if (checkHasV2[i] != 0) count++;
            }

            return count;
        }
    }
}
