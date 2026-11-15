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
    /// Every cell's arena object stays ACTIVE, deliberately: the compact field is meant to be
    /// seen as a whole (its pads sit on a grid with 1 m walkways). Registration is a separate
    /// switch: <see cref="LevelArenaHost"/> registers itself in its own <c>Update</c>, and the
    /// motor's obstacle tables are global, so the driver calls <see cref="ReleaseArena"/> on
    /// every cell but the first at load. That suspends the host's self-registration while
    /// leaving the floor, low walls and boxes visible; one active arena can never overflow the
    /// motor. <see cref="ApplyRegistration"/> resumes exactly one cell for a switch.
    /// </para>
    /// </remarks>
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class PhysicsTestCell : UdonSharpBehaviour
    {
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

        [Header("Activation rect (from placement level.bounds = this cell's footprint)")]
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
        [Tooltip("0 = abs<=, 1 = <=, 2 = >=, 3 = ==, 4 = abs>=")]
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

        // ---- operator seams ----------------------------------------------------

        /// <summary>
        /// Makes this cell the registered arena: allows its host to register and re-applies it
        /// (clear the motor's obstacle tables, then register ground + walls + boxes/ramps).
        /// </summary>
        /// <remarks>
        /// The GameObject is already active: the compact field shows every cell at once, and
        /// visibility is not the registration switch. The switch is the host's
        /// <c>SuspendRegistration</c> flag, so activating here is only a safety net for a scene
        /// that was saved with an inactive object.
        /// </remarks>
        public void ApplyRegistration()
        {
            if (arena == null) return;
            arena.gameObject.SetActive(true);
            arena.ResumeRegistration();
            arena.Reapply();
        }

        /// <summary>
        /// Stops this cell's arena from registering while keeping its floor, walls and boxes
        /// visible. The host's own Update is the only self-registration path, so suspending it
        /// is exactly "visible but not registered".
        /// </summary>
        public void ReleaseArena()
        {
            if (arena == null) return;
            arena.SuspendRegistration();
        }

        /// <summary>True when the arena is visible AND allowed to register.</summary>
        public bool IsArenaActive()
        {
            if (arena == null) return false;
            if (!arena.gameObject.activeSelf) return false;
            return !arena.IsRegistrationSuspended();
        }

        // ---- identity / spatial getters ---------------------------------------

        public string GetCellId()
        {
            if (cellId == null) return "";
            return cellId;
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

        /// <summary>
        /// True when (x, z) is inside the cell's rect grown by <paramref name="marginM"/>.
        /// </summary>
        /// <remarks>
        /// With <see cref="DistanceToFootprint"/> this is the design's activation rule
        /// (PHYSICS_TEST_FIELD section 3.3): nearest cell, grown by radius + margin, debounced.
        /// This batch's driver starts a cell explicitly instead, so these two are the executable
        /// form of the rect the importer bakes - not an unused copy of it.
        /// </remarks>
        public bool ContainsPoint(float x, float z, float marginM)
        {
            float halfX = footprintSize.x * 0.5f + marginM;
            float halfZ = footprintSize.y * 0.5f + marginM;
            return x >= footprintCenter.x - halfX && x <= footprintCenter.x + halfX &&
                   z >= footprintCenter.y - halfZ && z <= footprintCenter.y + halfZ;
        }

        /// <summary>Distance from (x, z) to the rect, 0 inside. Sorting key for "nearest cell".</summary>
        public float DistanceToFootprint(float x, float z)
        {
            float halfX = footprintSize.x * 0.5f;
            float halfZ = footprintSize.y * 0.5f;
            float dx = 0f;
            float dz = 0f;
            if (x < footprintCenter.x - halfX) dx = footprintCenter.x - halfX - x;
            else if (x > footprintCenter.x + halfX) dx = x - footprintCenter.x - halfX;
            if (z < footprintCenter.y - halfZ) dz = footprintCenter.y - halfZ - z;
            else if (z > footprintCenter.y + halfZ) dz = z - footprintCenter.y - halfZ;
            return Mathf.Sqrt(dx * dx + dz * dz);
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
    }
}
