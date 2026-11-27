using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRCKatamari.BallContact;
using VRCKatamari.BallLayers;
using VRCKatamari.Core;
using VRCKatamari.Diagnostics;
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
        [SerializeField] private BallPerformanceDiagnostics performanceDiagnostics;

        // ---- Fixed-step clock (provisional; Docs/Design/BallMotor/README.md 未定项) ----
        [SerializeField] private float fixedStepSeconds = 0.02f;
        [SerializeField] private int maxStepsPerFrame = 5;
        [SerializeField] private float stuckTimeoutSeconds = 1.5f;

        // ---- BallMotionMath inputs (DRAFT placeholders from Docs/Design/BallMotion/README.md) ----
        [SerializeField] private float maxSpeedMps = 4f;
        // Inertia, owner 2026-11-17, all PROVISIONAL (Docs/Design/BallMotor/BALL_INERTIA.md).
        // The m/s and m/s^2 values are given AT THE START DIAMETER D0 and are multiplied by
        // s = D / D0 (CurrentSizeScale); friction, grip and turn rate are time constants
        // and are not scaled.
        [SerializeField] private float accelerationMps2 = 7.5f;
        [SerializeField] private float brakingMps2 = 8f;             // 只用于反推
        [SerializeField] private float frictionPerSecond = 0f;
        [SerializeField] private float rollingResistanceMps2 = 1.9f; // 松手时的匀减速
        [SerializeField] private float lateralGripPerSecond = 2.9f;   // 横向速度衰减
        [SerializeField] private float turnRateDegreesPerSecond = 110f;
        [SerializeField] private float maxLateralMps = 1.2f;

        // ---- Ground plane: the always-present contact source where no terrain answers ----
        [SerializeField] private Vector3 groundPlaneNormal = new Vector3(0f, 1f, 0f);
        [SerializeField] private float groundPlaneOffset = 0f;

        // ---- Terrain support (static box tops, the step rule, gravity fall) ----
        // PROVISIONAL values, all from Docs/Design/BallMotor/README.md「关卡几何宿主 LevelArenaHost」节.
        //
        // Cell size of the uniform XZ grid the motor builds over the terrain mesh at
        // SetTerrainMesh time (TerrainGridMath). 1 m matches the v2 document's terrain.grid
        // unit; a refused grid is reported and the query falls back to the full scan, so
        // this is a speed knob only.
        [SerializeField] private float terrainGridCellM = 1f;

        // Terrain mesh contact (LEVEL_FORMAT_V2 §13). Default FALSE: with it off no terrain
        // array is queried. The mesh is the extra 3D-ground source; it does not replace the
        // ground plane, boxes or ramps.
        [SerializeField] private bool terrainMeshContactEnabled = false;
        // The RATIO, never a height: stepLimit = ratio * semantic diameter. 0.25 is the
        // value already in Docs/Design/Maps/PicnicLawn/layout.json and in the map
        // validator's climb_step rule. The radius form is deliberately absent (see
        // SupportSurfaceMath.TryStepHeightLimit).
        [SerializeField] private float stepHeightOverBallDiameter = 0.25f;

        // ---- Static ramps (support only; registered as slab OBBs) ----
        // The guard-rail on a ramp's slope, degrees (PROVISIONAL), applied by AddRamp. A
        // steeper ramp is refused with a reason code, never registered (the ball would
        // climb a wall) and never silently ignored (the ball would pass through).
        [SerializeField] private float maxSupportSlopeDegrees = SupportSurfaceMath.DefaultMaxSupportSlopeDegrees;

        // ---- Rest, then slide (BALL_INERTIA.md section 4), PROVISIONAL ----
        // The downhill component starts only after the ball has been below
        // rampRestSpeedMps * s for rampRestSeconds with no push, and never applies to a moving
        // ball. The slope must be steeper than rampSlideMinSlopeDegrees (flat ground never
        // slides) and within maxSupportSlopeDegrees (a steeper face is a wall or a climb).
        // The acceleration is g * sin(slope) * rampTangentialGravityScale * s (s = D / D0), the
        // same in diameters per second squared at every size.
        [SerializeField] private float rampTangentialGravityScale = 1f;
        [SerializeField] private float rampRestSpeedMps = 0.05f;
        [SerializeField] private float rampRestSeconds = 0.3f;
        [SerializeField] private float rampSlideMinSlopeDegrees = 2f;
        [SerializeField] private float gravityMps2 = 9.81f;

        // ---- Radial proxy (pin) admission (PROVISIONAL, unmeasured) ----
        // EVERY attached prop registers into the 16 pins (the original's rolling model);
        // there is no long-object admission test. A prop whose farthest corner does not
        // clear the core by this much cannot contribute a support above the core, so it is
        // not registered.
        [SerializeField] private float minProxyProtrusionM = 0.01f;

        // ---- SizeModelMath inputs: fixed-point mm^3 (Docs/OPEN_DECISIONS.md (r) C+D) ----
        // The ledger quantity is now the ball's GROWTH in cubic millimetres (1 LSB = 1 mm^3),
        // and the ball's total volume is startVolumeMm3 + confirmed. The old calibration
        // constants are DELETED, not renamed: `cubicMetersPerVolumeUnit` (0.0041 m^3/unit)
        // and `baselineVolumeUnits` (3.4390244 units) made "one unit" the growth currency,
        // which is exactly the 29%-of-the-start-ball quantum WHY_OUR_GROWTH_IS_BIG.md
        // diagnoses. A scene still serializing the old names has those dead keys ignored
        // (Unity's field-name contract), and the C# defaults below take over.
        //
        // startVolumeMm3 is the START BALL: pi/6 * D0^3 * 1e9. 14,094,798 = the 0.2997 m
        // calibration ball. A level that sets startDiameterWorldM has this recomputed in
        // Start (the one conversion), so the per-level start stays a diameter in the data.
        [SerializeField] private long startVolumeMm3 = 14094798L;
        // Global semantic diameter in metres (owner 2026-11-18). The serialized
        // mirror is kept for scene migration/read-back, not as a level setting.
        public const float GlobalMaxBallDiameterM = 400f;
        [SerializeField, HideInInspector] private float maxBallDiameterM = GlobalMaxBallDiameterM;
        [SerializeField] private float collisionToCoreRadiusRatio = 1f;
        [SerializeField] private float semanticDiameterPerCoreDiameter = 1f;

        // ---- Start size: the level-level parameter (parity D6) ----
        // The original's start diameter is a per-level value: 5 cm for MAS1, 22.2 cm for
        // Cancer, 30 cm for our first level (Docs/Design/Parity/PARITY_AUDIT.md, D6 and
        // §5.4 A/B). Before this field, the only start-size input was
        // baselineVolumeUnits, so "the level wants a 5 cm ball" could not be expressed and
        // every level started at the calibration's 0.2997 m.
        //
        // 0 means NOT SET: the level keeps startVolumeMm3 as authored, which is every
        // scene written before this field existed, so existing levels do not move. A
        // positive value is authoritative: ApplyStartDiameterWorldM rewrites startVolumeMm3
        // from it in Start, once, and from then on every consumer reads one start volume.
        // The diameter is the SEMANTIC diameter (the narrative size a player reads, see
        // SizeModelMath.TryDerive), not the collision diameter.
        [SerializeField] private float startDiameterWorldM = 0f;

        // ---- PickupAbsorbMath inputs ----
        [SerializeField] private float contactRadiusWorldM = 0f;
        // The size gate's per-item factor and the ball's diameter are the ONLY gate inputs,
        // and neither is serialized here: k travels with the candidate
        // (PickupFieldHost.propVolumeK -> SubmitCandidateWithVolumeKMm3), and the diameter is
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
        // 64 m, not 8: the eye is base + perRadius*radius, so a 15 m radius (the 30 m
        // cap) needs 0.55 + 2.4*15 = 36.55 m of lift and 0.75 + 2.2*15 = 33.75 m of back.
        // 8 m was sized for the retired 2.20 m ball and would pin the eye inside a big ball.
        [SerializeField] private float eyeUpMaxMeters = 64f;
        [SerializeField] private float eyeBaseBackMeters = 0.75f;
        [SerializeField] private float eyeBackPerRadius = 2.2f;
        [SerializeField] private float eyeMaxBackM = 64f;

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
        // The level's start diameter as the size model reports it at zero confirmed volume
        // (TryDerive(startVolumeMm3, 0)): the fixed diameter of the drawn core, which never
        // grows with absorbs. Written once in Start; 0 until then, which BallView reads as
        // "motor not ready, draw nothing yet". Neither a round reset nor a bank changes it:
        // startVolumeMm3 is the level's, not the round's.
        private float startSemanticDiameterWorldM;
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
        // All three are the ledger's fixed-point mm^3. `unbanked` is the ledger read-back,
        // `carried` is already-scored residual volume, and `confirmed` is their integer sum
        // (never accumulated in float - that is why this step exists).
        private long unbankedVolumeUnits;
        private long carriedVolumeMm3;
        private long confirmedVolumeMm3;
        private long bankedScoreUnits;
        private int absorbedCount;

        // ---- FollowRig filter state ----
        private Vector3 followRigWorldPosition;
        private float followRigYawDegrees;
        private float followRigHeightMeters;
        private bool hasFollowRigFrame;

        // 「减弱自动转向」（HubSettingsMath.ReduceAutoRotate）：开启时跟随架追的是限速后的
        // 朝向 comfortTargetYaw，不是 headingDegrees。只影响表现层；设置按修订号轮询。
        [Tooltip("Hub settings store; ReduceAutoRotate slows how fast the rider's view turns with the ball. Null = off.")]
        [SerializeField] private VRCKatamari.Hub.HubSettingsHost settings;
        private int settingsRevision = -1;
        private bool reduceAutoRotate;
        private float comfortTargetYaw;

        // ---- Input stage state (the intent layer owns the debounce latches) ----
        private float inputForward;
        private float inputTurn;
        private float inputLateral;

        // ---- Dash state (CONTACT_MODEL_V2 §3.8, owner 2026-11-18: hold to charge, release) ----
        // Allocated on first use like the candidate buffers: the input adapter's Update/event
        // can run before this component's Start, and BeginDashCharge must work then instead of
        // being dropped. The rules (tiers, spin, energy, huff) are DashMath's.
        private float[] dashState;

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

        // ---- Static geometry capacities ----
        // 8 planes stay (activity walls + the cell's own pads, LEVEL_FORMAT_V2 §5.4).
        // 64 static boxes through AddBox: the v2 format's own budget for
        // `structures[role=box]` is 64 and its `caps.staticSolids` is 256 (the OBB table).
        // The static box SURFACE ids are 1 + i, the ramp base is FirstRampSurfaceId (1000) to
        // keep the namespaces disjoint; props are 100 + i.
        private const int MaxPlaneCount = 8;
        private const int MaxBoxCount = 64;
        private const int ContactCapacity = 4;

        // ---- Cross-behaviour set buffers: ownership (why Start is not the only allocator) ----
        // Every buffer below holds a SET that another behaviour writes through a public
        // entry point: the pickup field queues candidates, and the arena host registers
        // planes/boxes/ramps. Unity does not order Start across GameObjects,
        // and this project has already met both orders (F1: a set arrived before
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
        //      reference and never a partial write: a ramp records its refusal code, the
        //      candidate queue logs once
        //      and answers false, and AddPlane/AddBox - which have no reason channel - answer
        //      false for the arena's existing report. It cannot be grown at that point: the
        //      accepted set and its readers hold these exact arrays, so replacing them would
        //      silently empty the ball's world. Nothing is written by a refused call.
        // The sets covered here are the scene geometry (planes, boxes, ramps) and the
        // candidate queue. The contact
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
        private int boxCount;
        // ---- Static ramps: support-only oriented planes ----
        // AddRamp validates the board (slope guard, capacity 4) and registers its slab OBB;
        // only the count and the refusal state are kept here.
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

        // ---- Support state ----
        // The contact solver writes the selected support (WriteContactSupport); the
        // diagnostics, the pickup host and the lab read it. The previous step's selected
        // support is the band reference and the rise base.
        private float rampRestTimerSeconds;
        private bool rampSliding;
        private bool previousStepRejected;
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
        // Gravity fall state. There is no jump and no upward velocity: the only way in
        // is losing support, and arriving at a surface is the only way out.
        private bool isFalling;
        private float fallSpeedMps;

        // ---- Terrain mesh (T0/T1; caller-owned fixed arrays, copied in once) ----
        private Vector3[] terrainVertices;
        private int[] terrainIndices;
        private int terrainVertexCount;
        private int terrainTriangleCount;
        private int[] terrainChunkStarts;
        private int[] terrainChunkTriangleCounts;
        private int[] terrainChunkZoneIds;
        private Vector3[] terrainChunkMin;
        private Vector3[] terrainChunkMax;
        private int terrainChunkCount;
        private bool terrainMeshReady;

        // ---- Terrain grid index (built once in SetTerrainMesh; never in the frame) ----
        // Per-triangle XZ AABB + surface id: the candidate query reads four floats instead of
        // computing eight Mathf.Min/Max per triangle per probe.
        private float[] terrainTriangleMinX;
        private float[] terrainTriangleMaxX;
        private float[] terrainTriangleMinZ;
        private float[] terrainTriangleMaxZ;
        private int[] terrainTriangleSurfaceId;
        // CSR: cell k owns terrainCellTriangles[terrainCellStart[k] .. terrainCellStart[k+1]).
        private int[] terrainCellStart;
        private int[] terrainCellTriangles;
        private int[] terrainCellCursor;
        private float terrainGridOriginX;
        private float terrainGridOriginZ;
        // terrainGridCellM (the built cell size) IS the serialized knob above: the build
        // clamps it in place, so a reader sees what was actually used.
        private int terrainGridCellsX;
        private int terrainGridCellsZ;
        private int terrainGridCellCount;
        private bool terrainGridReady;
        private int terrainGridRefusalCode;
        private int terrainGridRefusalDetail;
        // One candidate list per step, shared by the support ring and the T1 push-out.
        private int[] terrainCandidates;
        private bool[] terrainCandidateMark;
        // Triangles too big for the cells: offered to every query (window filtered), so a
        // few huge faces neither refuse the grid nor need to be stamped into 72+ cells.
        private int[] terrainOversizedTriangles;
        private int terrainOversizedCount;
        // Book-keeping: how many marks are currently set (0 when clear). NOT a diagnostic
        // reading - see the three per-query counters below.
        private int terrainCandidateCount;
        // ---- Per-query candidate counts (LAST step; the reading is not frame-accumulated) ----
        // Support and penetration collect separately (at different centres, and in v2 with
        // different windows), so one shared number would misreport whichever query ran last.
        private int terrainSupportCandidateCount;
        // The v2 contact sweep window (r + half the step travel + skin).
        private int terrainContactCandidateCount;
        // ---- Terrain cost diagnostics (per LAST fixed step; see ResetTerrainCostCounters) ----
        // The total is kept for existing readers; the split says whether the support ring or
        // the T1 push-out is the expensive half, and the two work counters say whether the
        // cost is "too many triangles" (trianglesTested) or "too much work per triangle"
        // (fullEvals / trianglesTested).
        private float lastTerrainQueryMicros;
        private float lastTerrainSupportMicros;
        private int terrainTrianglesTested;
        private int terrainFullEvals;
        // Steps the last Update ran. A frame's terrain cost is lastTerrainQueryMicros times
        // this, and Update zeroes both when it runs no step at all.
        private int stepsThisFrame;

        // ---- Pending absorb candidates; candidate collection is host-side ----
        private const int PendingCandidateCapacity = 4;

        private bool[] pendingAbsorbable;
        private string[] pendingKinds;
        // The item's OWN three edge lengths (the original's a, b, c) and its world rotation
        // at submission. The size gate reads the size alone; the contact test places that
        // box in the world with the rotation (the item's collision model touching the ball).
        private Vector3[] pendingSizes;
        private Quaternion[] pendingRotations;
        private Vector3[] pendingPositions;
        // The item's finished growth in mm^3 (penalty * k * a*b*c, priced once at import or
        // by the host fallback), not a float "value". This is the ledger gain.
        private long[] pendingGrowthMm3;
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
        private int[] pendingOutcomes;
        private float[] pendingBoxDistances;
        private float[] pendingContactLimits;
        private bool[] pendingCommitted;
        private long[] pendingCommitGains;
        private bool[] pendingCommitLogFailures;
        private int[] pendingClaimFailures;
        private int[] pendingResolveFailures;
        private int[] pendingClaimCountsAtFailure;

        // ---- Absorbed-event ring: the only "which item was eaten" exit ----
        // The pickup stage and the ledger know an item was consumed, but until this
        // ring existed nothing outside the motor could learn WHICH item disappeared
        // (GetAbsorbedCount is a total). A presentation or host layer needs the
        // identity, the world position and the size to hide the prop that was just
        // eaten, so the commit path appends one event per committed absorb.
        // Purely additive: no stage of the pipeline reads the ring, and a full ring
        // evicts the oldest event instead of failing (the absorb already committed).
        private const int AbsorbedEventCapacity = AttachmentStepMath.EventCapacity;

        private string[] absorbedEventItemIds;
        private Vector3[] absorbedEventPositions;
        // Attachment payload is captured at the gate, never reconstructed from a later
        // Update's moving ball/item transforms. Parallel, fixed-capacity Udon buffers.
        private Vector3[] absorbedEventLocalPositions;
        private Quaternion[] absorbedEventLocalRotations;
        private Vector3[] absorbedEventBallCenters;
        private float[] absorbedEventCollisionRadii;
        private float[] absorbedEventContactRadii;
        private int[] absorbedEventSteps;
        private bool[] absorbedEventPoseValid;
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
        private const int TraceLineLimit = 400;
        private const int AbsorbGapLogLimit = 400;
        private int absorbGapLogged;
        private int traceLinesLogged;

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

        // ---- Blocking-box refusals, deliberately NOT part of the overflow signal ----
        // "The box capacity was exceeded" and "a mis-wired scene dropped an obstacle" are
        // different failures: the first is a normal per-scan answer from a host that found
        // more nearby props than this ball can touch, the second means the scene is wrong.
        // Counting them together would make a crowded picnic table look like a broken
        // scene forever. Same shape as the overflow signal (a count and one reason string,
        // no per-frame logging); the previous box set stays registered on a refusal.

        // ---- Radial proxy shell: the ball's 16 pins (one owner; the layers stay stateless) ----
        // The shell lives on the motor because the motor already owns the ball pose and
        // is the only place that feeds the ground solve. The arrays are allocated once in
        // Start and reused: 16 directions, 16 endpoint-only supports (the pins), and a
        // ledger of every registered prop (8 AABB corners each, per-item slices) so a detach
        // or a rebuild can re-derive the table. EVERY attached prop registers (original-first:
        // the original's rolling model is 16 pins that any absorbed prop lengthens); the table
        // is a max over the corners, so one attach is an incremental merge, never a rescan.
        // Capacity: ProxyItemCapacity props (256: the 0.80 m ball of the offline estimate
        // carries about 190); a prop past it is refused and counted (ProxySkipPoolFull).
        private const int ProxyItemCapacity = AttachmentStepMath.ProxyItemCapacity;
        private const int ProxyEndpointCapacity = AttachmentStepMath.ProxyEndpointCapacity;

        private Vector3[] proxyDirections;
        private float[] proxySupports;
        private Vector3[] proxyEndpointPool;
        private int proxyEndpointCount;
        private int[] proxyItemStarts;
        private int[] proxyItemLengths;
        private string[] proxyItemKeys;
        private bool[] proxyItemIsCandidate;
        private int proxyItemCount;
        // The math layer's long-object candidate list, kept at length 0: this shell has no
        // candidates (every prop is an ordinary item), and a zero-length list is the layer's
        // documented way to forbid them.
        private string[] proxyCandidateKeys;
        private int proxyCandidateCount;
        private int[] proxyAffectedIndices;
        private float[] proxySolveScratch;
        private Vector3[] proxyCorners;
        private Vector3[] proxyRemovedCorners;
        private int proxySkipCount;
        private int proxySkipReason;
        private int proxyRefusalCount;
        private int proxyRefusalLogCount;

        // Why AttachRadialProxy did not register a prop. 2 is an ordinary skip (the prop
        // does not clear the core, so it cannot lengthen any pin); 4..6 are capacity and
        // scene/data problems.
        private const int ProxySkipNone = AttachmentStepMath.ProxySkipNone;
        private const int ProxySkipInsideCore = AttachmentStepMath.ProxySkipInsideCore;
        private const int ProxySkipPoolFull = AttachmentStepMath.ProxySkipPoolFull;
        private const int ProxySkipInvalidInput = AttachmentStepMath.ProxySkipInvalidInput;
        private const int ProxySkipShellRefused = AttachmentStepMath.ProxySkipShellRefused;


        // ---- Ball contact v2 (Docs/Design/BallMotor/CONTACT_MODEL_V2.md; S3) ----
        //
        // PROVISIONAL. ONE contact solver (StepContactV2) owns the ball's position. No
        // number in this region is measured or play-tested; the values come from
        // ContactTuning.

        // Collider proxies (Docs/Design/PickupAbsorb/COLLIDER_PROXY.md §6): when true the
        // prop block is read from the proxy columns instead of the prop OBB columns. FALSE
        // keeps the prop OBB columns; the proxy buffers are allocated anyway so a runtime
        // flip finds them.
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
        /// <summary>
        /// A stuck episode lasts while the ball stays inside this radius of the point the
        /// episode started at. A wedged ball does not hold still: the solver trades the
        /// two faces' normal components every few steps, so single steps of 1-3 mm
        /// interleave with 0.3 mm ones, and a per-step progress threshold keeps resetting.
        /// 1 cm is three times the jitter span measured on a 50-degree wedge.
        /// </summary>
        private const float ContactStuckConfineM = 0.01f;
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
        // Bumped whenever the whole static table is dropped (ClearObbs). A host that registered
        // solids remembers the value it saw, so a later per-solid remove or restore can tell
        // that the table it wrote into has since been replaced by another level's.
        private int staticObbEpoch;
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
        private float[] contactTopYV2;
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
        [SerializeField] private bool climbDiagnosticsEnabled;
        private float nextClimbDiagnosticsSeconds;
        private float slopeStamina = ContactTuning.SlopeStaminaMax;
        private float slopeUphillSeconds;
        private float slopeRestSeconds;
        private bool slopeSliding;
        private bool climbActive;
        private float climbAnchorY;
        private int climbSurfaceId;
        private float climbCooldown;
        private bool climbLocked;
        private float climbAlongSpeed;
        private float climbHeightWorldM;
        private float climbSpeedWorldMps;
        // The climb surface's own top and plane, remembered across steps so the climb can
        // finish the last radius above a low face (TERRAIN_CLIMB 6.3). TryStep owns them.
        private float climbTopYWorldM;
        private Vector3 climbSurfaceNormalWorld;
        private Vector3 climbSurfacePointWorld;
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
        private Vector3 contactStuckAnchor;
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
        private bool loggedEmptyAbsorbWhitelist;
        private float maxCollisionRadiusWorldM;

        private void Start()
        {
            Debug.Log("[BallMotor] Start begin center=" + startCenterWorld);
            if (fixedStepSeconds <= 0f) fixedStepSeconds = 0.02f;
            if (maxStepsPerFrame < 1) maxStepsPerFrame = 1;
            if (stuckTimeoutSeconds <= 0f) stuckTimeoutSeconds = 1.5f;
            EnsureDashState();

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
            absorbedEventLocalPositions = new Vector3[AbsorbedEventCapacity];
            absorbedEventLocalRotations = new Quaternion[AbsorbedEventCapacity];
            absorbedEventBallCenters = new Vector3[AbsorbedEventCapacity];
            absorbedEventCollisionRadii = new float[AbsorbedEventCapacity];
            absorbedEventContactRadii = new float[AbsorbedEventCapacity];
            absorbedEventSteps = new int[AbsorbedEventCapacity];
            absorbedEventPoseValid = new bool[AbsorbedEventCapacity];
            absorbedEventCount = 0;
            absorbedEventRingCount = 0;

            traceLinesLogged = 0;

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

            // The support pair starts on the configured ground plane; the first step
            // writes the real surface.
            supportSurfaceId = SupportSurfaceMath.GroundSurfaceId;
            supportSurfacePointWorld = new Vector3(0f, groundPlaneOffset, 0f);
            supportSurfaceNormalWorld = new Vector3(0f, 1f, 0f);
            supportSurfaceHeightWorldM = groundPlaneOffset;
            supportCandidateCount = 1;
            stepHeightLimitWorldM = 0f;
            previousSupportSurfaceId = SupportSurfaceMath.NoSurfaceId;
            previousSupportHeightWorldM = 0f;
            lastSupportHeightWorldM = 0f;
            isFalling = false;
            fallSpeedMps = 0f;

            // The radial proxy shell is the ball's 16 pins: fixed directions whose support
            // length is the max over every attached prop's box corners. State is owned
            // here; the attach/detach/rebuild protocol is RadialProxyShellMath and the pure
            // support math stays in RadialProxyMath. An empty shell is the documented
            // core-sphere case.
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
            proxyCandidateKeys = new string[0];
            proxyCandidateCount = 0;
            proxyAffectedIndices = new int[RadialProxyMath.DirectionCount];
            proxySolveScratch = new float[RadialProxyMath.DirectionCount];
            proxyCorners = new Vector3[RadialProxyShellMath.AabbCornerCount];
            proxyRemovedCorners = new Vector3[RadialProxyShellMath.AabbCornerCount];
            proxySkipCount = 0;
            proxySkipReason = ProxySkipNone;
            proxyRefusalCount = 0;
            proxyRefusalLogCount = 0;
            RadialProxyShellMath.TryClearShell(proxyDirections, proxySupports,
                ref proxyEndpointCount, ref proxyItemCount, proxyCandidateKeys,
                ref proxyCandidateCount);

            // The contact tables are allocated here only when they do not exist yet: a
            // host that registered before this Start ran (component order between
            // GameObjects is arbitrary) has already forced the allocation, and
            // re-allocating would wipe its scene geometry.
            if (staticObbCenter == null) AllocateContactV2Buffers();

            allowedKinds = new string[0];
            allowedKindCount = 0;

            ballCenterWorld = startCenterWorld;
            planarVelocity = Vector3.zero;
            headingDegrees = 0f;
            visualRollRotation = Quaternion.identity;
            if (ballVisualRollRoot != null) visualRollRotation = ballVisualRollRoot.rotation;
            discontinuous = true;

            // The one global cap (owner 2026-11-18) is a SEMANTIC diameter; its collision
            // radius is derived through the same ratios as every other size, so the reach
            // clamp and the growth saturate cannot disagree at the top end. The serialized
            // mirror is forced here because old scenes can still carry a per-level value
            // (30 m, or Cancer's 1.2 m) from before this decision.
            maxBallDiameterM = GlobalMaxBallDiameterM;
            maxCollisionRadiusWorldM = (0.5f * maxBallDiameterM / semanticDiameterPerCoreDiameter)
                * collisionToCoreRadiusRatio;

            if (GrowthLedgerMath.TryJoin(roundState, playerIds, confirmedVolumes, bankedScores,
                lifeStates, respawnAtSet, respawnAt, playerGenerations, ref playerCount,
                localPlayerId))
            {
                localRow = 0;
                hasLocalRow = true;
            }
            if (autoStartPlaying) TryBeginPlaying(0f);

            // Parity D6: the start diameter is a per-level parameter. A level that sets
            // startDiameterWorldM rewrites startVolumeMm3 from it here, before any consumer
            // reads the start volume; a level that does not keeps the serialized
            // startVolumeMm3 (every scene authored before the field existed, i.e. the
            // 0.2997 m start ball). Since steps C+D the ledger quantity is mm^3; there is
            // no per-unit calibration left to change.
            ApplyStartDiameterWorldM();

            // The start-volume mapping is the ball's start size: it is the baseline for the
            // growth fraction the motion layer consumes, and the floor the conversion
            // shrink clamps to.
            float coreRadius;
            float collisionRadius;
            float semanticDiameter;
            long effectiveConfirmedMm3;
            if (SizeModelMath.TryDerive(startVolumeMm3, 0L, maxBallDiameterM,
                collisionToCoreRadiusRatio, semanticDiameterPerCoreDiameter,
                out coreRadius, out collisionRadius, out semanticDiameter,
                out effectiveConfirmedMm3))
            {
                baseCoreRadiusWorldM = coreRadius;
                startSemanticDiameterWorldM = semanticDiameter;
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

            Debug.Log("[BallMotor] Start ready diameter=" + semanticDiameterWorldM
                + " collisionRadius=" + collisionRadiusWorldM
                + " startVolumeMm3=" + startVolumeMm3
                + " startSize=" + (startDiameterWorldM > 0f ? "level" : "calibration")
                + " hasLocalRow=" + hasLocalRow);
        }

        /// <summary>
        /// Makes the level's start diameter the ball's start volume, or keeps the serialized
        /// <see cref="startVolumeMm3"/> when the level does not set one.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Parity D6: the original's start diameter is a per-level value (5 cm for MAS1,
        /// 22.2 cm for Cancer, 0.2997 m for our first level). Since steps C+D the growth
        /// quantity is a fixed-point mm^3 volume, so this is the ONE conversion from the
        /// human-facing diameter to that volume: <c>V = pi/6 * (D/ratio)^3 * 1e9</c>, rounded
        /// once. 0 means unset, so a scene written before the field existed keeps the
        /// serialized start volume (14,094,798 mm^3 = the 0.2997 m ball).
        /// </para>
        /// <para>
        /// A start ball above the cap is refused and the authored start volume kept:
        /// writing a start above the cap would make the derive saturate at the cap and the
        /// level would silently start smaller than it asked for.
        /// </para>
        /// </remarks>
        private void ApplyStartDiameterWorldM()
        {
            if (!KatamariMath.IsFiniteFloat(startDiameterWorldM) || startDiameterWorldM <= 0f)
            {
                return;
            }
            if (!KatamariMath.IsFiniteFloat(maxBallDiameterM) || maxBallDiameterM <= 0f)
            {
                return;
            }

            long volumeMm3;
            if (!SizeModelMath.TrySphereVolumeMm3(startDiameterWorldM,
                semanticDiameterPerCoreDiameter, out volumeMm3))
            {
                Debug.LogWarning("[BallMotor] startDiameterWorldM=" + startDiameterWorldM
                    + " is not usable; keeping startVolumeMm3=" + startVolumeMm3);
                return;
            }
            if (startDiameterWorldM > maxBallDiameterM)
            {
                Debug.LogWarning("[BallMotor] startDiameterWorldM=" + startDiameterWorldM
                    + " is above the cap " + maxBallDiameterM
                    + "; keeping startVolumeMm3=" + startVolumeMm3);
                return;
            }
            startVolumeMm3 = volumeMm3;
        }

        /// <summary>
        /// Replaces the level's start volume at run time, so one motor can serve levels
        /// whose start balls differ (a hub that hosts several maps). Returns false and
        /// changes nothing when the volume is not usable.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This only rewrites the level baseline: <c>startVolumeMm3</c>, the base core
        /// radius and the start semantic diameter that <see cref="GetStartDiameterWorldM"/>
        /// reports. It does NOT resize the ball that is on screen and does not touch the
        /// ledger; the caller follows it with <see cref="TryResetForNewRound"/>, which
        /// re-derives the round's size from this baseline (Start does the same derive once
        /// for the serialized value). Calling it mid-round without a reset leaves the live
        /// size, which was derived from the old start volume, until the next recompute.
        /// </para>
        /// <para>
        /// Priority versus <c>startDiameterWorldM</c>: that serialized field is consumed once,
        /// in Start (<c>ApplyStartDiameterWorldM</c> rewrites <c>startVolumeMm3</c> from it);
        /// nothing reads it afterwards. So a value passed here always wins from then on, and
        /// the authored diameter only decides the size before the first switch.
        /// </para>
        /// <para>
        /// A start at or above the global cap is refused, same as the authored
        /// <c>startDiameterWorldM</c>: the derive would saturate and the level would start
        /// smaller than it asked for. Before Start has run the motor has no cap-ready
        /// state, so the call is refused.
        /// </para>
        /// </remarks>
        public bool SetStartVolumeMm3(long volumeMm3)
        {
            if (volumeMm3 <= 0L) return false;
            if (!hasLocalRow) return false;

            float coreRadius;
            float collisionRadius;
            float semanticDiameter;
            long effectiveConfirmedMm3;
            if (!SizeModelMath.TryDerive(volumeMm3, 0L, maxBallDiameterM,
                collisionToCoreRadiusRatio, semanticDiameterPerCoreDiameter,
                out coreRadius, out collisionRadius, out semanticDiameter,
                out effectiveConfirmedMm3))
            {
                return false;
            }
            if (semanticDiameter >= maxBallDiameterM) return false;

            startVolumeMm3 = volumeMm3;
            baseCoreRadiusWorldM = coreRadius;
            startSemanticDiameterWorldM = semanticDiameter;
            return true;
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
        /// every reader of them hold these exact arrays, so re-allocating would silently empty the ball's world.
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
        /// shorter one, and it is the undersized case.
        /// </para>
        /// <para>
        /// The capacities are this component's own constants
        /// (<see cref="MaxPlaneCount"/>, <see cref="MaxBoxCount"/>), so "a full set" is decided in one
        /// place. Lengths are compared with <c>&lt;</c> rather than equality, so a longer
        /// array stays usable; nothing writes past a capacity.
        /// </para>
        /// </remarks>
        private bool RegistrationBuffersAtCapacity()
        {
            if (planeNormalBuffer == null || planeOffsetBuffer == null) return false;
            if (planeNormalBuffer.Length < MaxPlaneCount) return false;
            if (planeOffsetBuffer.Length < MaxPlaneCount) return false;
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
            planeCount = 0;
            boxCount = 0;
            rampCount = 0;
            rampRefusalCount = 0;
            rampRefusalCode = SupportSurfaceMath.RampRefusalNone;
            rampRefusalSlopeDegrees = 0f;
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
                pendingSizes = new Vector3[PendingCandidateCapacity];
                pendingRotations = new Quaternion[PendingCandidateCapacity];
                pendingPositions = new Vector3[PendingCandidateCapacity];
                pendingGrowthMm3 = new long[PendingCandidateCapacity];
                pendingVolumeK = new float[PendingCandidateCapacity];
                pendingItemIds = new string[PendingCandidateCapacity];
                pendingGenerations = new int[PendingCandidateCapacity];
                pendingAccepted = new bool[PendingCandidateCapacity];
                pendingSubmitTimes = new float[PendingCandidateCapacity];
                pendingOutcomes = new int[PendingCandidateCapacity];
                pendingBoxDistances = new float[PendingCandidateCapacity];
                pendingContactLimits = new float[PendingCandidateCapacity];
                pendingCommitted = new bool[PendingCandidateCapacity];
                pendingCommitGains = new long[PendingCandidateCapacity];
                pendingCommitLogFailures = new bool[PendingCandidateCapacity];
                pendingClaimFailures = new int[PendingCandidateCapacity];
                pendingResolveFailures = new int[PendingCandidateCapacity];
                pendingClaimCountsAtFailure = new int[PendingCandidateCapacity];
                pendingCandidateCount = 0;
                candidateBuffersReady = true;
            }
            return CandidateBuffersAtCapacity();
        }

        /// <summary>
        /// Allocates the dash state the first time it is read or charged.
        /// </summary>
        /// <remarks>
        /// The input adapters' Update can run before this component's Start (Udon start order
        /// between GameObjects is arbitrary), so <see cref="BeginDashCharge"/> cannot assume
        /// Start ran. Start calls this too; whichever call runs first allocates and initialises
        /// the state (full energy, no charge).
        /// </remarks>
        private void EnsureDashState()
        {
            if (dashState != null) return;
            dashState = new float[DashMath.StateLength];
            DashMath.TryReset(dashState);
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
            if (pendingSizes == null || pendingPositions == null) return false;
            if (pendingRotations == null) return false;
            if (pendingGrowthMm3 == null || pendingVolumeK == null) return false;
            if (pendingItemIds == null) return false;
            if (pendingGenerations == null || pendingAccepted == null) return false;
            if (pendingSubmitTimes == null) return false;
            if (pendingAbsorbable.Length < PendingCandidateCapacity) return false;
            if (pendingKinds.Length < PendingCandidateCapacity) return false;
            if (pendingSizes.Length < PendingCandidateCapacity) return false;
            if (pendingRotations.Length < PendingCandidateCapacity) return false;
            if (pendingPositions.Length < PendingCandidateCapacity) return false;
            if (pendingGrowthMm3.Length < PendingCandidateCapacity) return false;
            if (pendingVolumeK.Length < PendingCandidateCapacity) return false;
            if (pendingItemIds.Length < PendingCandidateCapacity) return false;
            if (pendingGenerations.Length < PendingCandidateCapacity) return false;
            if (pendingAccepted.Length < PendingCandidateCapacity) return false;
            if (pendingSubmitTimes.Length < PendingCandidateCapacity) return false;
            return true;
        }

        private void Update()
        {
            bool profile = performanceDiagnostics != null && performanceDiagnostics.sampling;
            long frameStart = profile ? BallPerformanceMath.Timestamp() : 0L;
            localClockSeconds = localClockSeconds + Time.deltaTime;
            fixedStepAccumulator = fixedStepAccumulator + Time.deltaTime;

            // Cap the backlog instead of replaying it: a long frame must not turn
            // into a burst of catch-up steps that the player experiences as a jump.
            float maximumAccumulator = fixedStepSeconds * (float)maxStepsPerFrame;
            if (fixedStepAccumulator > maximumAccumulator)
            {
                fixedStepAccumulator = maximumAccumulator;
            }

            // A frame that runs no step has no terrain cost either, and the pair
            // (lastTerrainQueryMicros, stepsThisFrame) must stay consistent so a reader can
            // multiply them without a zero-step frame looking like a free one.
            stepsThisFrame = 0;
            ResetTerrainCostCounters();
            int steps = 0;
            while (fixedStepAccumulator >= fixedStepSeconds && steps < maxStepsPerFrame)
            {
                fixedStepAccumulator = fixedStepAccumulator - fixedStepSeconds;
                StepBall(fixedStepSeconds);
                steps = steps + 1;
            }
            long poseStart = profile ? BallPerformanceMath.Timestamp() : 0L;
            ApplyPresentation();
            if (profile)
            {
                long frameEnd = BallPerformanceMath.Timestamp();
                performanceDiagnostics._SampleElapsed(BallPerformanceMath.PoseWrites, poseStart, frameEnd);
                performanceDiagnostics._SampleElapsed(BallPerformanceMath.MotorFrame, frameStart, frameEnd);
                performanceDiagnostics._Sample(BallPerformanceMath.BallDiameter, semanticDiameterWorldM);
                performanceDiagnostics._Sample(BallPerformanceMath.StepsPerFrame, stepsThisFrame);
                performanceDiagnostics._Sample(BallPerformanceMath.StaticObb, staticObbCount);
            }
        }

        /// <summary>
        /// Runs one fixed step through the nine stages in their fixed order.
        /// </summary>
        /// <remarks>
        /// Stages 1-2 (intent, motion) and 5-9 (absorb, ledger, size, growth push-out,
        /// visual roll, follow rig) surround the one position authority,
        /// <see cref="StepContactV2"/> (Docs/Design/BallMotor/CONTACT_MODEL_V2.md §7.3).
        /// </remarks>
        private void StepBall(float dt)
        {
            // The contact solve does not query the terrain mesh through the old per-step
            // counters, so they read zero. The reading is the LAST step only: a frame's cost is the reading times the
            // number of steps that frame ran (see GetStepsThisFrame).
            ResetTerrainCostCounters();
            stepsThisFrame = stepsThisFrame + 1;
            authorityFrame = authorityFrame + 1;

            // 1. ControlIntentMath: the only place a raw axis becomes an intent,
            //    and the only place a menu suppresses it.
            bool profile = performanceDiagnostics != null && performanceDiagnostics.sampling;
            long phaseStart = profile ? BallPerformanceMath.Timestamp() : 0L;
            StepInput();
            if (profile) phaseStart = BallPerformanceMath.Split(performanceDiagnostics, BallPerformanceMath.Input, phaseStart);

            // 2. 首步注入真实出射速度；冲刺中仍走普通推进、转向和阻力。
            float sizeScale = CurrentSizeScale();
            float diameterM = semanticDiameterWorldM;
            float baseSpeedMps = maxSpeedMps * sizeScale;
            if (DashMath.TryBrake(dashState, inputForward)) Debug.Log("[input] dash ended: brake");
            bool charging = DashMath.IsCharging(dashState);
            float speedScale;
            float accelerationScale;
            DashMath.DriveScales(dashState, diameterM, out speedScale, out accelerationScale);
            Vector3 requestedDisplacement = Vector3.zero;
            Vector3 nextVelocity;
            float nextHeading;
            Vector3 stepDisplacement;
            if (DashMath.TryTakeLaunch(dashState, headingDegrees, baseSpeedMps, speedScale, out nextVelocity))
            {
                planarVelocity = nextVelocity;
                requestedDisplacement = nextVelocity * dt;
            }
            else if (BallMotionMath.TryStep(charging ? 0f : inputForward,
                charging ? 0f : inputTurn, charging ? 0f : inputLateral,
                headingDegrees, planarVelocity, dt, sizeScale, maxSpeedMps * speedScale,
                accelerationMps2 * accelerationScale, brakingMps2, frictionPerSecond, rollingResistanceMps2,
                lateralGripPerSecond, turnRateDegreesPerSecond, maxLateralMps,
                out nextHeading, out nextVelocity, out stepDisplacement))
            {
                headingDegrees = nextHeading;
                planarVelocity = nextVelocity;
                requestedDisplacement = stepDisplacement;
            }

            // 2.4. 空转只转球壳和吸附物，不扫动骑手视角，也不改变出射朝向。
            if (DashMath.IsSpinning(dashState, diameterM))
            {
                float spinDegrees = DashMath.SpinRateDegreesPerSecond * dt;
                visualRollRotation = Quaternion.Euler(0f, spinDegrees, 0f) * visualRollRotation;
            }

            if (profile) phaseStart = BallPerformanceMath.Split(performanceDiagnostics, BallPerformanceMath.Motion, phaseStart);

            // 2.5 - 4. The position authority for this step: it writes the final centre,
            //     the smooth camera anchor and the visual-roll axis the stages below read.
            Vector3 groundedCenter = Vector3.zero;
            Vector3 followCenter = Vector3.zero;
            Vector3 supportNormal = groundPlaneNormal;
            bool hitWall;
            StepContactV2(dt, requestedDisplacement, speedScale, out groundedCenter, out followCenter,
                out supportNormal, out hitWall);

            if (profile) phaseStart = BallPerformanceMath.Split(performanceDiagnostics, BallPerformanceMath.Contact, phaseStart);

            // 5. PickupAbsorbMath: a decision per candidate, at the final position
            //    only, so nothing is absorbed through a wall or from the old centre.
            int pendingAtEvaluation = profile ? pendingCandidateCount : 0;
            EvaluatePendingCandidates(groundedCenter);

            // 6. GrowthLedgerMath: a decision becomes volume only through a commit.
            CommitAcceptedAbsorbs(groundedCenter, supportNormal);

            // 7. SizeModelMath: the three sizes are a function of the confirmed
            //    volume, never an accumulation of it. The confirmed total is
            //    recomputed by the one method that owns that identity, so the
            //    per-step stage and a conversion cannot disagree about it.
            RecomputeConfirmedAndSize();

            // 7.5 Growth push-out (design §4.2). Absorbing grows the radius inside one
            //     step, so the ball can end a step embedded in the wall it just grew into.
            ApplyGrowthPushOut(ref groundedCenter, ref followCenter, ref supportNormal);

            if (profile) phaseStart = BallPerformanceMath.Split(performanceDiagnostics, BallPerformanceMath.Absorb, phaseStart);

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

            // 以本步的球径快照推进计时；普通减速或坡面速度变低不结束冲刺。
            bool autoLaunched;
            bool wasDashing = DashMath.IsDashing(dashState);
            if (DashMath.TryStep(dashState, dt, diameterM, hitWall, out autoLaunched) && autoLaunched)
            {
                Debug.Log("[input] dash fired (full auto)");
            }
            // 撞墙那一步已经打过一行；寿命耗尽进入喘气才打这一行，保证每次冲刺只有一行结束日志。
            if (wasDashing && !hitWall && !DashMath.IsDashing(dashState) && DashMath.IsHuffed(dashState))
                Debug.Log("[input] dash ended: life out");
            if (profile)
            {
                BallPerformanceMath.Split(performanceDiagnostics, BallPerformanceMath.StepTail, phaseStart);
                performanceDiagnostics._Sample(BallPerformanceMath.PendingCandidates, pendingAtEvaluation);
            }
        }


        /// <summary>
        /// Allocates the contact tables once, in <c>Start</c>. About 25 KB in total, which
        /// is the design's budget (design §1.3).
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
            contactTopYV2 = new float[ContactTuning.ContactCapacity];
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

            slopeStamina = ContactTuning.SlopeStaminaMax;
            slopeUphillSeconds = 0f;
            slopeRestSeconds = 0f;
            slopeSliding = false;
            climbActive = false;
            climbAnchorY = 0f;
            climbSurfaceId = -1;
            climbCooldown = 0f;
            climbLocked = false;
            climbAlongSpeed = 0f;
            climbHeightWorldM = 0f;
            climbSpeedWorldMps = 0f;
            climbTopYWorldM = 0f;
            climbSurfaceNormalWorld = new Vector3(0f, 0f, 1f);
            climbSurfacePointWorld = Vector3.zero;
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
            contactStuckAnchor = ballCenterWorld;
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
        /// the readings the diagnostics report. A step is a contact whose normal points up,
        /// the query is exact, a ramp is a solid slab, and a protruding pin is a contact of
        /// its own.
        /// </para>
        /// <para>
        /// Failure is closed. A refused solve keeps the previous position and drops the
        /// velocity instead of moving the ball with a value nobody could compute; a
        /// residual beyond 2 cm rejects the step's position (design §2.1); a residual
        /// beyond the radius is a deep embed and recovers to the last stable point
        /// (design §2.8).
        /// </para>
        /// </remarks>
        private void StepContactV2(float dt, Vector3 requestedDisplacement, float dashSpeedScale,
            out Vector3 groundedCenter, out Vector3 followCenter, out Vector3 supportNormal,
            out bool hitWall)
        {
            double stepStartSeconds = WallClockSeconds();
            bool profile = performanceDiagnostics != null && performanceDiagnostics.sampling;
            contactV2ClockSeconds = contactV2ClockSeconds + dt;
            groundedCenter = ballCenterWorld;
            followCenter = ballCenterWorld;
            supportNormal = groundPlaneNormal;
            hitWall = false;

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
            long shapeStart = profile ? BallPerformanceMath.Timestamp() : 0L;
            PrepareNeedleShape(radius);
            if (profile) BallPerformanceMath.Split(performanceDiagnostics, BallPerformanceMath.AttachmentShape, shapeStart);

            // The drive the climb gate reads is the motion layer's TARGET speed, not the
            // acceleration-limited request: a ball standing against a wall has no actual
            // speed and would otherwise never start a climb (design §3.2). A dash raises
            // that target speed through the same DashMath multiplier, so the existing
            // v_gate chain (GateSpeed -> ClimbMath/SlopeClimbMath) sees the dash without a
            // second dash-aware path.
            float driveSpeed;
            Vector3 intentDirection;
            ComputeDrive(dashSpeedScale, out contactDriveDirection, out intentDirection, out driveSpeed);

            // A climb whose gate has closed (input released, or the push turned away)
            // leaves the channel here, so the ball slides back instead of hovering
            // (design §3.2/§3.3; pass6 C09/C14).
            RefreshClimbGate(driveSpeed);

            // Rest, then slide: the latch and the downhill component read last step's
            // support and this step's push; the added speed takes effect in this solve.
            UpdateRampSlide(dt);

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
            long arenaStart = profile ? BallPerformanceMath.Timestamp() : 0L;
            FillNearWindow(radius, maxTravel);
            if (profile) BallPerformanceMath.Split(performanceDiagnostics, BallPerformanceMath.ArenaNearObb, arenaStart);

            Vector3 center = ballCenterWorld;
            Vector3 planar = planarVelocity;
            Vector3 beforeContact = planar;
            // 撞墙判定要「求解前」的爬墙状态：本步里爬墙通道接管的面，正是被撞的面。
            bool climbWasActive = climbActive;
            int climbWasSurfaceId = climbSurfaceId;
            float bottomBeforeStep = ballCenterWorld.y - radius;
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
            // The terrain contact source (TERRAIN_CLIMB 6.3) is collected LAST, after the
            // support query below: both write the SAME per-step candidate array, so the list
            // the solver reads must be the last one written. See the collection site.
            bool terrainContacts = terrainMeshContactEnabled && terrainMeshReady &&
                terrainTriangleCount > 0;
            bool proxyActive = ProxyContactActive();
            int[] proxyKinds = proxyActive ? nearObbKind : null;
            float[] proxyRadii = proxyActive ? nearObbRadius : null;
            float[] proxyHalfHeights = proxyActive ? nearObbHalfHeight : null;
            int[] proxyAxes = proxyActive ? nearObbAxis : null;
            int[] proxyHullStarts = proxyActive ? nearObbHullStart : null;
            int[] proxyHullCounts = proxyActive ? nearObbHullCount : null;
            int[] proxyFaceStarts = proxyActive ? nearObbFaceStart : null;
            int[] proxyFaceCounts = proxyActive ? nearObbFaceCount : null;
            // "There is no support plane where there is terrain" (LEVEL_FORMAT_V2 1.3): the
            // arena ground plane is a CONTACT SOURCE in v2, and a ball standing in a pit
            // below that plane would read the plane as a metre-deep penetration and be
            // pushed up out of the terrain. So where the mesh answers under the
            // ball's footprint for THIS step, the plane is not a source; where it does not
            // (a seam, the edge of the mesh, a level without terrain) it is the fallback.
            bool meshAnswersHere = false;
            if (terrainContacts)
            {
                float supportLimit;
                if (!SupportSurfaceMath.TryStepHeightLimit(semanticDiameterWorldM,
                    stepHeightOverBallDiameter, out supportLimit)) supportLimit = 0f;
                long supportGridStart = profile ? BallPerformanceMath.Timestamp() : 0L;
                terrainSupportCandidateCount = CollectTerrainCandidates(ballCenterWorld,
                    collisionRadiusWorldM);
                if (profile) BallPerformanceMath.Split(performanceDiagnostics, BallPerformanceMath.TerrainGrid, supportGridStart);
                int supportCandidates = terrainSupportCandidateCount;
                int terrainSupportId;
                Vector3 terrainSupportNormal;
                float terrainSupportHeight;
                meshAnswersHere = TryQueryTerrainSupport(ballCenterWorld,
                    ballCenterWorld.y - collisionRadiusWorldM, supportLimit, supportCandidates,
                    out terrainSupportId, out terrainSupportNormal, out terrainSupportHeight);
                if (profile)
                {
                    performanceDiagnostics._Sample(BallPerformanceMath.TerrainSupport, lastTerrainSupportMicros * 0.001f);
                    performanceDiagnostics._Sample(BallPerformanceMath.TerrainSupportVisited, terrainTrianglesTested);
                    performanceDiagnostics._Sample(BallPerformanceMath.TerrainSupportFullTests, terrainFullEvals);
                }
            }
            // THE TERRAIN CONTACT LIST, collected NOW: the ball's footprint plus half of this
            // step's travel plus the contact skin, so a contact at the end of the step is
            // still covered. This is the LAST write to terrainCandidates before the solver
            // below - the support query (above) used the same array, and TryStep reads
            // (array, count) as one pair, so collecting here is what keeps them together.
            // Re-collecting anything between this and TryStep would hand the solver the
            // support list with the sweep count (dropped and duplicated candidates).
            int terrainContactCandidates = -1;
            if (terrainContacts)
            {
                float terrainTravel = Mathf.Sqrt(requestedDisplacement.x *
                    requestedDisplacement.x + requestedDisplacement.y *
                    requestedDisplacement.y + requestedDisplacement.z *
                    requestedDisplacement.z);
                Vector3 terrainWindowCenter = new Vector3(
                    ballCenterWorld.x + requestedDisplacement.x * 0.5f,
                    ballCenterWorld.y + requestedDisplacement.y * 0.5f,
                    ballCenterWorld.z + requestedDisplacement.z * 0.5f);
                long contactGridStart = profile ? BallPerformanceMath.Timestamp() : 0L;
                terrainContactCandidateCount = CollectTerrainCandidates(terrainWindowCenter,
                    radius + terrainTravel * 0.5f + ContactTuning.ContactSkinM);
                if (profile) BallPerformanceMath.Split(performanceDiagnostics, BallPerformanceMath.TerrainGrid, contactGridStart);
                terrainContactCandidates = terrainContactCandidateCount;
                if (profile)
                {
                    if (terrainContactCandidates >= 0)
                        performanceDiagnostics._Sample(BallPerformanceMath.TerrainSolveCandidates, terrainContactCandidates);
                    performanceDiagnostics._Sample(BallPerformanceMath.TerrainSolveFullScan, terrainContactCandidates < 0 ? 1f : 0f);
                }
            }
            bool solved = ContactSolveMath.TryStep(!meshAnswersHere, groundPlaneNormal, groundPlaneOffset,
                planeNormalBuffer, planeOffsetBuffer, planeCount, nearObbCenter, nearObbAxisX,
                nearObbAxisY, nearObbAxisZ, nearObbHalfExtents, nearObbSurfaceId, nearObbRole,
                nearObbCount, ref center, radius, ref planar, ref vertical, ref climbActive,
                ref climbAnchorY, ref climbSurfaceId, ref climbCooldown, ref climbLocked,
                ref climbAlongSpeed, ref climbTopYWorldM, ref climbSurfaceNormalWorld,
                ref climbSurfacePointWorld, ref slopeStamina, ref slopeUphillSeconds,
                ref slopeRestSeconds, ref slopeSliding, contactDriveDirection, driveSpeed, dt, protrusion,
                contactNormalsV2, contactPointsV2, contactDepthsV2, contactAnglesV2,
                contactSurfaceIdsV2, contactRolesV2, contactFeaturesV2, contactClassesV2,
                ContactTuning.ContactCapacity, out contactCount, out dominantSurfaceId,
                out dominantClass, out climbHeight, out climbSpeed, out substepCount,
                out residual, out airborne, out reportNormal, ContactTuning.KappaSubstep,
                ContactTuning.MaxSubsteps, 0f, ContactTuning.ContactSkinM, proxyKinds,
                proxyRadii, proxyHalfHeights, proxyAxes, proxyHullStarts, proxyHullCounts,
                proxyFaceStarts, proxyFaceCounts, propProxyHullPoints, propProxyFaceNormals,
                propProxyFaceOffsets, propProxyFaceA, propProxyFaceB, propProxyFaceC,
                contactTopYV2,
                terrainContacts ? terrainCandidates : null, terrainContactCandidates,
                terrainContacts ? terrainTriangleMinX : null,
                terrainContacts ? terrainTriangleMaxX : null,
                terrainContacts ? terrainTriangleMinZ : null,
                terrainContacts ? terrainTriangleMaxZ : null,
                terrainContacts ? terrainTriangleSurfaceId : null,
                terrainContacts ? terrainVertices : null,
                terrainContacts ? terrainIndices : null,
                terrainContacts ? terrainTriangleCount : 0,
                terrainContacts ? terrainChunkMin : null,
                terrainContacts ? terrainChunkMax : null,
                terrainContacts ? terrainChunkStarts : null,
                terrainContacts ? terrainChunkTriangleCounts : null,
                terrainContacts ? terrainChunkZoneIds : null,
                terrainContacts ? terrainChunkCount : 0,
                CurrentSizeScale(), rampRestSeconds, rampRestSpeedMps * CurrentSizeScale());
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

            // A residual beyond 2 cm rejects the whole motion, not only its position.
            // Keeping the solver's velocity while rolling the centre back banked thrust
            // every fixed step (E3 A2: no movement, |v|=5.015), then launched the ball
            // when the obstruction vanished. Accepted steps keep their tangential inertia.
            previousStepRejected = false;
            if (ContactSolveMath.RejectUnresolvedMotion(ballCenterWorld, residual,
                ContactResidualRejectM, ref center, ref planar, ref vertical))
            {
                contactRejectedStepCount = contactRejectedStepCount + 1;
                previousStepRejected = true;
                climbAlongSpeed = 0f;
                climbSpeedWorldMps = 0f;
            }

            // The solver's velocity is the step's velocity from here on. Everything below
            // that edits the members (the needle response, the deep-embed and stuck
            // recoveries, the fall speed WriteContactSupport reads) works on the fields,
            // so the write-back has to come first: after them it would overwrite a
            // recovery's cleared velocity with the pre-recovery one and a "teleport and
            // clear" would keep its speed (design §2.8, rule 2).
            planarVelocity = planar;
            contactVerticalVelocity = vertical;

            // Needle tips are swept segments, not points (design §4A.3): they push the
            // whole ball out and merge into the contact log as their own records.
            ResolveNeedleContacts(ref center, radius, v2CoreContactCount);
            ApplyNeedleVelocityResponse(v2CoreContactCount);
            if (DashMath.IsDashing(dashState))
            {
                // 不做第二次查询：只看求解器本步的接触记录和实际水平速度变化。
                // 求解后记录的深度已被推回 0，所以不看深度；障碍高度取记录的顶面。
                for (int i = 0; i < v2CoreContactCount; i++)
                {
                    if (contactClassesV2[i] != ContactCodes.ClassClimb) continue;
                    float topY = contactTopYV2[i];
                    float obstacleHeight = topY >= float.MaxValue ? float.MaxValue : topY - bottomBeforeStep;
                    bool wasClimbingThisFace = climbWasActive && contactSurfaceIdsV2[i] == climbWasSurfaceId;
                    if (!DashMath.IsDashWallHit(contactNormalsV2[i], contactAnglesV2[i], obstacleHeight,
                        radius, beforeContact, planarVelocity, wasClimbingThisFace)) continue;
                    hitWall = true;
                    break;
                }
                if (hitWall)
                {
                    // 撞墙结束冲刺，爬墙通道不能顺势接管：关掉并进入冷却。
                    Debug.Log("[input] dash ended: wall hit speedBefore=" + beforeContact.magnitude
                        + " speedAfter=" + planarVelocity.magnitude);
                    climbActive = false;
                    climbCooldown = ContactTuning.ClimbCooldownS;
                    climbAlongSpeed = 0f;
                    climbSpeedWorldMps = 0f;
                    climbHeightWorldM = 0f;
                }
            }

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
            UpdateContactRecovery(dt, intentDirection, ref center);

            // Opt-in, once per simulation second. Planar speed alone cannot diagnose a
            // vertical climb: report accepted centre progress alongside the channel state.
            if (climbDiagnosticsEnabled && contactV2ClockSeconds >= nextClimbDiagnosticsSeconds)
            {
                nextClimbDiagnosticsSeconds = contactV2ClockSeconds + 1f;
                Vector3 acceptedMove = center - ballCenterWorld;
                Debug.Log("[BallMotor] climb t=" + contactV2ClockSeconds
                    + " climbActive=" + climbActive + " capLock=" + climbLocked
                    + " id=" + climbSurfaceId + " height=" + climbHeightWorldM
                    + " climbV=" + climbSpeedWorldMps + " planarV=" + planarVelocity.magnitude
                    + " actualSpeed=" + (acceptedMove.magnitude / dt)
                    + " stamina=" + slopeStamina + " uphillS=" + slopeUphillSeconds
                    + " slopeSliding=" + slopeSliding + " resid=" + contactResidualMax);
            }

            groundedCenter = center;
            // v2 has no proxy lift, so the camera anchor IS the ball centre: the design
            // wants the eye to follow a climb (design §4.4).
            followCenter = center;

            contactPreviousCenterWorld = center;
            contactPreviousRotation = visualRollRotation;
            contactHasPreviousFrame = true;

            lastStepMicros = ElapsedMicros(stepStartSeconds);
        }

        /// <summary>
        /// The "rest, then slide" downhill component on the current support. The latch is
        /// <see cref="RampSlideMath"/>; this method only gathers its inputs and carries the
        /// velocity back.
        /// </summary>
        /// <remarks>
        /// The support is last step's (<c>WriteContactSupport</c>): only a surface the ball
        /// is rolling on counts, so a falling ball, a climb contact or a pin-only support
        /// is not a slope. A step the solver rejected adds nothing (and its velocity was
        /// zeroed), so a blocked ball cannot bank slope speed.
        /// </remarks>
        private void UpdateRampSlide(float dt)
        {
            float scale = CurrentSizeScale();
            bool onSlope = false;
            bool supported = !isFalling && !climbActive && !supportFromClimb &&
                !supportFromNeedle && supportSurfaceId != SupportSurfaceMath.NoSurfaceId;
            if (supported)
            {
                float normalY = Mathf.Clamp(supportSurfaceNormalWorld.y, -1f, 1f);
                float slopeDegrees = Mathf.Acos(normalY) * Mathf.Rad2Deg;
                onSlope = slopeDegrees > rampSlideMinSlopeDegrees &&
                    slopeDegrees <= maxSupportSlopeDegrees;
            }
            float planarSpeed = Mathf.Sqrt(planarVelocity.x * planarVelocity.x
                + planarVelocity.z * planarVelocity.z);
            float driveIntent = DriveIntentMagnitude();
            float nextRest;
            bool nextSliding;
            if (!RampSlideMath.TryStepRestLatch(rampRestTimerSeconds, rampSliding,
                planarSpeed, rampRestSpeedMps * scale, driveIntent, onSlope, dt,
                rampRestSeconds, out nextRest, out nextSliding))
            {
                rampRestTimerSeconds = 0f;
                rampSliding = false;
                return;
            }
            rampRestTimerSeconds = nextRest;
            rampSliding = nextSliding;
            if (!rampSliding || previousStepRejected || rampTangentialGravityScale <= 0f) return;

            Vector3 slopeVelocity;
            if (SupportSurfaceMath.TryApplyRampTangentialGravity(planarVelocity,
                supportSurfaceNormalWorld, gravityMps2 * scale, rampTangentialGravityScale, dt,
                maxSpeedMps * scale, out slopeVelocity))
            {
                planarVelocity = BallMotionMath.HoldLongitudinalSlope(planarVelocity,
                    slopeVelocity, headingDegrees, rollingResistanceMps2 * scale * dt);
            }
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
        /// s = D / D0: the current semantic diameter over the START diameter. Every
        /// length-type motion value, the climb gate and the slope slide acceleration scale
        /// with it. 1 until the start size is known.
        /// </summary>
        private float CurrentSizeScale()
        {
            if (!KatamariMath.IsFiniteFloat(startSemanticDiameterWorldM) ||
                startSemanticDiameterWorldM <= 0f) return 1f;
            if (!KatamariMath.IsFiniteFloat(semanticDiameterWorldM) ||
                semanticDiameterWorldM <= 0f) return 1f;
            return semanticDiameterWorldM / startSemanticDiameterWorldM;
        }

        /// <summary>
        /// The player's drive intent magnitude, for the ramp rest latch and the push-blocked
        /// test: no stick drive during charge; a running burst counts as forward drive.
        /// </summary>
        private float DriveIntentMagnitude()
        {
            if (DashMath.IsCharging(dashState)) return 0f;
            return Mathf.Abs(inputForward) + Mathf.Abs(inputLateral);
        }

        /// <summary>
        /// The motion layer's target speed and the direction the driver pushes in
        /// (design §3.2). The target speed is recomputed from the motion layer's own
        /// three inputs because BallMotionMath does not export it; the expression is its
        /// speed cap times the forward intent, and the speed cap includes the dash
        /// multiplier (so the climb gate sees a dashing ball's higher target speed).
        /// </summary>
        /// <remarks>
        /// <paramref name="direction"/> is the way the ball is MOVING (its planar velocity,
        /// the facing only when it is at rest), which is what the climb gate and the
        /// protrusion estimate want. <paramref name="intentDirection"/> is the way the
        /// player is PUSHING: the forward and lateral axes taken along the current
        /// heading, signed, unit length, or zero without input. A wall deflects the
        /// velocity (a ball wedged between two faces swaps sideways every step) but not
        /// the intent, so the recovery's "is this push blocked" test reads the intent.
        /// </remarks>
        private void ComputeDrive(float dashSpeedScale, out Vector3 direction, out Vector3 intentDirection,
            out float speed)
        {
            // 蓄力滑行不是推力；冲刺只提高有输入时的目标速度，首步初速度由接触层直接读取。
            bool charging = DashMath.IsCharging(dashState);
            float headingRadians = headingDegrees * Mathf.Deg2Rad;
            float forwardIntent = charging ? 0f : Mathf.Clamp(inputForward, -1f, 1f);
            float lateralIntent = charging ? 0f : Mathf.Clamp(inputLateral, -1f, 1f);
            Vector3 intentSum = new Vector3(
                Mathf.Sin(headingRadians) * forwardIntent
                    + Mathf.Cos(headingRadians) * lateralIntent,
                0f,
                Mathf.Cos(headingRadians) * forwardIntent
                    - Mathf.Sin(headingRadians) * lateralIntent);
            float intentLength;
            if (!KatamariMath.TryNormalize(intentSum, out intentDirection, out intentLength))
            {
                intentDirection = Vector3.zero;
            }

            float speedCap = maxSpeedMps * CurrentSizeScale() * dashSpeedScale;
            speed = speedCap * BallMotionMath.DriveSpeedFraction(forwardIntent);
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
                    if (!BallShapeMath.TryNeedleSegmentPlaneContact(planeNormalBuffer[i],
                        planeOffsetBuffer[i], tipPrevious, tipNow, tipRadius, out planeNormal,
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
                // The tangent plane's height under the centre.
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
            // point/normal.
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
            // Slopes keep their channel for stop/rest/slide, even after effective drive
            // vanishes. The solver, not this wall-release gate, owns that transition.
            if (SlopeClimbMath.IsSlope(ObbContactMath.ContactAngleDeg(climbSurfaceNormalWorld))) return;
            // The gate is checked against the face the SOLVER is climbing (the remembered
            // climb surface, kept stable by the selection hysteresis), not against
            // "whichever wall the velocity pointed at last step" (climbWallNormalWorld):
            // in a narrow V that record flips between the two faces with the velocity, and
            // a face the push has turned away from would release a climb the solver is
            // still entitled to (TERRAIN_CLIMB 6.6).
            float normalX = climbSurfaceNormalWorld.x;
            float normalZ = climbSurfaceNormalWorld.z;
            float horizontal = Mathf.Sqrt(normalX * normalX + normalZ * normalZ);
            if (horizontal <= 0f) return;
            float inverse = 1f / horizontal;
            float pushIn = Mathf.Max(0f, -(contactDriveDirection.x * normalX * inverse
                + contactDriveDirection.z * normalZ * inverse));
            float planarSpeed = Mathf.Sqrt(planarVelocity.x * planarVelocity.x
                + planarVelocity.z * planarVelocity.z);
            float vGate = ClimbMath.GateSpeed(planarSpeed, driveSpeed, CurrentSizeScale());
            if (pushIn >= ContactTuning.PMin &&
                vGate >= ClimbMath.RequiredSpeed(
                    ObbContactMath.ContactAngleDeg(climbSurfaceNormalWorld))) return;
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
        private void UpdateContactRecovery(float dt, Vector3 intentDirection,
            ref Vector3 center)
        {
            float moveX = center.x - ballCenterWorld.x;
            float moveY = center.y - ballCenterWorld.y;
            float moveZ = center.z - ballCenterWorld.z;
            float progress = Mathf.Sqrt(moveX * moveX + moveY * moveY + moveZ * moveZ);
            float driveIntent = DriveIntentMagnitude();

            // A wall the PLAYER pushes into is BLOCKED, not stuck. Counting a
            // wall press as stuck made the soft stage call ClearClimbState (killing the
            // climb) and three episodes entered the recovery hold - the 2026-11-16 RefCancer
            // pit: 18 s of forward into a building wall, recoveries=3, frozen.
            // The push is the INTENT direction, not the velocity direction: a ball wedged
            // between two faces has its velocity deflected sideways and flipping every
            // step, which read as "pushing into the face" and hid a real wedge.
            bool blockedByContact = false;
            for (int i = 0; i < v2ContactCount; i++)
            {
                float normalX = contactNormalsV2[i].x;
                float normalZ = contactNormalsV2[i].z;
                float horizontal = Mathf.Sqrt(normalX * normalX + normalZ * normalZ);
                if (horizontal <= 1e-4f) continue;
                float inverse = 1f / horizontal;
                float into = intentDirection.x * normalX * inverse
                    + intentDirection.z * normalZ * inverse;
                if (into < -0.5f)
                {
                    blockedByContact = true;
                    break;
                }
            }

            // "Stuck" is: pushing, not blocked, and not leaving a small disc around where
            // the episode started (ContactStuckConfineM). The episode's start is the
            // position before the first step of the run (stuckSeconds is 0 between
            // episodes). A per-step displacement threshold cannot see a wedge: the
            // solver's side-to-side trade makes a 2-3 mm step every few steps.
            bool stuckStep = false;
            if (driveIntent > 0.05f && !blockedByContact)
            {
                if (stuckSeconds <= 0f) contactStuckAnchor = ballCenterWorld;
                float awayX = center.x - contactStuckAnchor.x;
                float awayY = center.y - contactStuckAnchor.y;
                float awayZ = center.z - contactStuckAnchor.z;
                stuckStep = awayX * awayX + awayY * awayY + awayZ * awayZ
                    < ContactStuckConfineM * ContactStuckConfineM;
            }
            if (stuckStep)
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
            slopeStamina = ContactTuning.SlopeStaminaMax;
            slopeUphillSeconds = 0f;
            slopeRestSeconds = 0f;
            slopeSliding = false;
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
                    groundPlaneOffset, planeNormalBuffer, planeOffsetBuffer, planeCount,
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
                groundPlaneOffset, planeNormalBuffer, planeOffsetBuffer, planeCount, nearObbCenter,
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
        /// Collects this step's candidate triangles for one centre: every mesh triangle whose
        /// grid cell the ball's footprint (± one collision radius, which covers both the
        /// 0.9 r support ring and T1's ± r sphere AABB) overlaps.
        /// </summary>
        /// <returns>
        /// The candidate count, or -1 when no grid exists (the queries then take -1 and scan
        /// every chunk, which is slower but never wrong).
        /// </returns>
        /// <remarks>
        /// One deduped list is what turns 17 probes x every triangle into 17 probes x a few
        /// dozen: the probes only walk this array. The list is rebuilt per query because the
        /// support query runs AFTER T1, whose push can move the centre; rebuilding is a few
        /// cell lookups and a few mark writes, far below the triangle scan it replaces.
        /// Nothing allocates: the buffers are the motor's fixed arrays.
        /// </remarks>
        private int CollectTerrainCandidates(Vector3 center, float halfExtent)
        {
            if (!terrainGridReady || terrainGridCellCount <= 0) return -1;
            TerrainGridMath.ClearCandidates(terrainCandidates, terrainCandidateCount,
                terrainCandidateMark);
            // -1 means "no candidate list": the build's buffers are sized to the mesh so the
            // collector's rejection paths cannot fire in production, but if they ever do the
            // queries read the negative count as the full scan, and the diagnostic shows -1
            // rather than a partial set that would silently change which surface supports.
            terrainCandidateCount = TerrainGridMath.CollectCandidates(center.x, center.z,
                halfExtent, terrainGridCellM, terrainGridOriginX,
                terrainGridOriginZ, terrainGridCellsX, terrainGridCellsZ,
                terrainGridCellCount, terrainCellStart, terrainCellTriangles,
                terrainTriangleMinX, terrainTriangleMaxX, terrainTriangleMinZ,
                terrainTriangleMaxZ, terrainOversizedTriangles, terrainOversizedCount,
                terrainTriangleCount, terrainCandidates, terrainCandidateMark);
            return terrainCandidateCount;
        }

        /// <summary>
        /// T0 support query: the highest mesh triangle under the ball's footprint ring that
        /// is inside the ball's step band, re-measured on its plane at the ball's XZ. The
        /// returned height is a surface height, exactly like a plane/box/ramp support.
        /// </summary>
        /// <remarks>
        /// The geometry and the band live in <see cref="TerrainMeshMath.TrySelectSupport"/>
        /// (one implementation, and EditMode-testable with real triangles); this method only
        /// binds the motor's arrays and accounts the query time. A false return means the
        /// mesh has nothing under this footprint inside the band, and the selection falls
        /// back to the arena ground plane.
        /// </remarks>
        private bool TryQueryTerrainSupport(Vector3 center, float ballBottom, float stepLimit,
            int candidateCount, out int surfaceId, out Vector3 normal, out float height)
        {
            double start = WallClockSeconds();
            bool found = TerrainMeshMath.TrySelectSupport(center, ballBottom, stepLimit,
                proxyDirections, visualRollRotation, collisionRadiusWorldM * 0.9f,
                terrainVertices, terrainIndices, terrainTriangleCount, terrainChunkMin,
                terrainChunkMax, terrainChunkStarts, terrainChunkTriangleCounts,
                terrainChunkZoneIds, terrainChunkCount, terrainCandidates, candidateCount,
                terrainTriangleMinX, terrainTriangleMaxX, terrainTriangleMinZ,
                terrainTriangleMaxZ, terrainTriangleSurfaceId, ref terrainTrianglesTested,
                ref terrainFullEvals, out normal, out height, out surfaceId);
            float micros = ElapsedMicros(start);
            lastTerrainSupportMicros = lastTerrainSupportMicros + micros;
            lastTerrainQueryMicros = lastTerrainQueryMicros + micros;
            return found;
        }

        /// <summary>
        /// Wall-clock seconds as a double: the one clock that advances DURING a frame.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <c>Time.time</c> is a frame timestamp, so two reads inside one step return the
        /// same value and BOTH per-step microsecond counters read 0: the terrain query
        /// counter on every step of the 2026-11-16 terrain run, and the v2 step counter
        /// (<see cref="GetLastStepMicros"/>) whenever the v2 authority is on.
        /// <c>Time.realtimeSinceStartupAsDouble</c> reads the actual clock;
        /// the double form is required because the float property's ulp is already ~0.24 ms
        /// after an hour of uptime, which is coarser than the query being measured.
        /// </para>
        /// <para>
        /// Cost: two native property reads per measured block, no allocation, no string, and
        /// nothing in any decision path reads the result - it is a diagnostic counter only.
        /// Against the loop it measures (up to 17 probes x the triangles of the hit chunks)
        /// that is below the noise floor of the Udon interpreter, so leaving it on in a
        /// shipped build is free. If a build's Udon whitelist lacks the AsDouble property,
        /// this ONE method is the only line to change (to <c>Time.realtimeSinceStartup</c>,
        /// accepting the coarser resolution).
        /// </para>
        /// </remarks>
        private double WallClockSeconds()
        {
            return Time.realtimeSinceStartupAsDouble;
        }

        /// <summary>Microseconds since <paramref name="startSeconds"/>; never negative.</summary>
        private float ElapsedMicros(double startSeconds)
        {
            double elapsed = WallClockSeconds() - startSeconds;
            if (elapsed < 0.0) elapsed = 0.0;
            return (float)(elapsed * 1000000.0);
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
            PickupAbsorbStepMath.EvaluatePendingCandidates(pendingAbsorbable, pendingKinds,
                pendingSizes, pendingRotations, pendingPositions, pendingGrowthMm3,
                pendingVolumeK, pendingCandidateCount, allowedKinds, allowedKindCount,
                center, collisionRadiusWorldM, contactRadiusWorldM, semanticDiameterWorldM,
                maxCollisionRadiusWorldM, pendingAccepted, pendingOutcomes,
                pendingBoxDistances, pendingContactLimits);
            for (int i = 0; i < pendingCandidateCount; i++)
            {
                AbsorbOutcome outcome = (AbsorbOutcome)pendingOutcomes[i];
                float boxDistance = pendingBoxDistances[i];
                if (debugTraceItemId != null && debugTraceItemId.Length > 0
                    && pendingItemIds[i] == debugTraceItemId
                    && traceLinesLogged < TraceLineLimit)
                {
                    traceLinesLogged = traceLinesLogged + 1;
                }
                if (!pendingAccepted[i])
                {
                    if (!loggedEmptyAbsorbWhitelist && allowedKindCount == 0
                        && outcome == AbsorbOutcome.RejectKindNotAllowed)
                    {
                        loggedEmptyAbsorbWhitelist = true;
                        Debug.LogWarning("[BallMotor] absorb kind whitelist is empty; all pickup kinds are rejected.");
                    }
                    RecordReject(pendingItemIds[i], (int)outcome, boxDistance);
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
        private void CommitAcceptedAbsorbs(Vector3 contactCenter, Vector3 supportNormal)
        {
            if (!PickupAbsorbStepMath.CommitAcceptedAbsorbs(hasLocalRow, pendingAccepted,
                pendingItemIds, pendingGenerations, pendingGrowthMm3, pendingCandidateCount,
                roundState, playerIds, confirmedVolumes, bankedScores, lifeStates,
                respawnAtSet, respawnAt, playerGenerations, playerCount, commitItemIds,
                commitGenerations, commitWinners, commitSequences, commitGains, ref commitCount,
                claimItemIds, claimGenerations, claimPlayerIds, claimFrames,
                claimRequestSequences, ref claimCount, predictedPlayerIds, predictedItemIds,
                predictedGenerations, predictedGains, ref predictedCount, roundId,
                localPlayerId, localRow, authorityFrame, ref absorbRequestSequence,
                ref unbankedVolumeUnits, ref bankedScoreUnits, ref absorbedCount,
                pendingCommitted, pendingCommitGains, pendingCommitLogFailures,
                pendingClaimFailures, pendingResolveFailures, pendingClaimCountsAtFailure))
            {
                LogCommitFailureOnce("(none)", 0, 0, 0, -1, claimCount);
                return;
            }
            for (int i = 0; i < pendingCandidateCount; i++)
            {
                if (pendingCommitLogFailures[i])
                    LogCommitFailureOnce(pendingItemIds[i], pendingCommitGains[i],
                        pendingClaimFailures[i], pendingResolveFailures[i], -1,
                        pendingClaimCountsAtFailure[i]);
                if (pendingCommitted[i]) RecordAbsorbedEvent(i, contactCenter, supportNormal);
            }
            PickupAbsorbStepMath.CompactPendingCandidates(pendingCommitted,
                ref pendingCandidateCount, pendingAbsorbable, pendingKinds, pendingSizes,
                pendingRotations, pendingPositions, pendingGrowthMm3, pendingVolumeK,
                pendingItemIds, pendingGenerations, pendingAccepted, pendingSubmitTimes);
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
            PickupAbsorbStepMath.CopyPendingCandidate(write, read, pendingAbsorbable,
                pendingKinds, pendingSizes, pendingRotations, pendingPositions, pendingGrowthMm3,
                pendingVolumeK, pendingItemIds, pendingGenerations, pendingAccepted,
                pendingSubmitTimes);
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
            PickupAbsorbStepMath.DeriveSize(startVolumeMm3, confirmedVolumeMm3,
                maxBallDiameterM, collisionToCoreRadiusRatio, semanticDiameterPerCoreDiameter,
                ref coreRadiusWorldM, ref collisionRadiusWorldM, ref semanticDiameterWorldM);
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
            PickupAbsorbStepMath.RecomputeConfirmedAndSize(unbankedVolumeUnits, carriedVolumeMm3,
                startVolumeMm3, maxBallDiameterM, collisionToCoreRadiusRatio,
                semanticDiameterPerCoreDiameter, ref confirmedVolumeMm3, ref coreRadiusWorldM,
                ref collisionRadiusWorldM, ref semanticDiameterWorldM);
        }

        private void UpdateFollowRig(Vector3 center, float dt)
        {
            Vector3 rigPosition;
            float rigYaw;
            float height;
            RefreshComfortSettings();
            float targetYaw = headingDegrees;
            if (reduceAutoRotate && hasFollowRigFrame && !discontinuous)
            {
                targetYaw = FollowRigMath.ComfortTargetYaw(comfortTargetYaw, headingDegrees,
                    FollowRigMath.ComfortYawRateDegreesPerSecond, FollowRigMath.ComfortMaxLagDegrees, dt);
            }
            comfortTargetYaw = targetYaw;
            if (!FollowRigMath.TryUpdate(heightFilterHz, yawFilterHz, center,
                semanticDiameterWorldM * 0.5f, targetYaw,
                dt, eyeBaseUpMeters, eyeUpPerRadius, eyeUpMinMeters, eyeUpMaxMeters,
                eyeBaseBackMeters, eyeBackPerRadius, eyeMaxBackM, discontinuous,
                hasFollowRigFrame, followRigYawDegrees,
                followRigHeightMeters, out rigPosition, out rigYaw, out height))
            {
                return;
            }
            followRigWorldPosition = rigPosition;
            followRigYawDegrees = rigYaw;
            followRigHeightMeters = height;
            hasFollowRigFrame = true;
        }

        private void RefreshComfortSettings()
        {
            if (settings == null)
            {
                reduceAutoRotate = false;
                return;
            }
            int revision = settings.GetRevision();
            if (revision == settingsRevision) return;
            settingsRevision = revision;
            reduceAutoRotate = settings.IsReduceAutoRotate();
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
            long resetEffectiveConfirmedMm3;
            if (!SizeModelMath.TryDerive(startVolumeMm3, 0L, maxBallDiameterM,
                collisionToCoreRadiusRatio, semanticDiameterPerCoreDiameter,
                out resetCoreRadius, out resetCollisionRadius, out resetSemanticDiameter,
                out resetEffectiveConfirmedMm3))
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
            unbankedVolumeUnits = 0L;
            carriedVolumeMm3 = 0L;
            confirmedVolumeMm3 = 0L;
            bankedScoreUnits = 0;
            lastAwardFailure = 0;
            // awardedKeys / awardedPoints / awardedCount were cleared by the ledger rule.

            coreRadiusWorldM = resetCoreRadius;
            collisionRadiusWorldM = resetCollisionRadius;
            semanticDiameterWorldM = resetSemanticDiameter;

            inputForward = 0f;
            inputTurn = 0f;
            inputLateral = 0f;
            ControlIntentMath.TryReset(previousDown, lastActionTime, comfortState);
            // A new round starts with no dash and no cooldown carried over.
            DashMath.TryReset(dashState);

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

        /// <summary>
        /// 开始长按蓄力；已经蓄力、正在冲刺、喘气或没有寿命时拒绝。
        /// </summary>
        /// <remarks>
        /// The two input adapters call this when the button goes down - the VR adapter on the
        /// left hand's <c>InputUse</c> (trigger), the desktop adapter on Shift. The rules
        /// (tier time, spin, energy, huff) are <see cref="DashMath"/>'s and are identical on
        /// both platforms. This method owns no device read and no gate: the caller applies the
        /// ride and round gates, so a standing player or a finished round cannot charge.
        /// </remarks>
        public bool BeginDashCharge()
        {
            EnsureDashState();
            return DashMath.TryBeginCharge(dashState);
        }

        /// <summary>
        /// 空转后、自动出射前松手会触发弱冲刺；否则只结束蓄力或忽略松手。
        /// </summary>
        public int ReleaseDashCharge()
        {
            EnsureDashState();
            float held = DashMath.ChargeSeconds(dashState);
            int released = DashMath.ReleaseCharge(dashState, semanticDiameterWorldM);
            if (released == DashMath.ReleaseNone)
            {
                Debug.Log("[input] dash release ignored (no active charge)");
            }
            else
            {
                string outcome = released == DashMath.ReleaseHelpDash
                    ? "dash fired (early release)" : "dash released before spin";
                Debug.Log("[input] " + outcome + " held=" + held + "s spin=" +
                    DashMath.SpinSecondsFor(semanticDiameterWorldM) + "s full=" +
                    GetDashChargeRequiredSeconds() + "s");
            }
            return released;
        }

        /// <summary>关门、离座或重生：取消蓄力和待出射/进行中的冲刺，不清零惯性。</summary>
        public void CancelDash()
        {
            DashMath.Cancel(dashState);
        }

        /// <summary>Whether the button is held and the charge is running.</summary>
        public bool IsDashCharging()
        {
            return DashMath.IsCharging(dashState);
        }

        /// <summary>
        /// 是否达到当前球径的空转阈值；空转只影响表现，不改变平移规则。
        /// </summary>
        public bool IsDashSpinning()
        {
            return DashMath.IsSpinning(dashState, semanticDiameterWorldM);
        }

        /// <summary>The hold time this ball's size needs for a full charge, seconds.</summary>
        public float GetDashChargeRequiredSeconds()
        {
            return DashMath.ChargeSecondsFor(semanticDiameterWorldM);
        }

        /// <summary>Whether a burst is running this step.</summary>
        public bool IsDashing()
        {
            return DashMath.IsDashing(dashState);
        }

        /// <summary>Whether the huff is running (all charge input refused, 8 s).</summary>
        public bool IsDashHuffed()
        {
            return DashMath.IsHuffed(dashState);
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
        public bool SubmitCandidate(bool absorbable, string kind, Vector3 size,
            Vector3 worldPosition, float valueUnits, string itemInstanceId, int itemGeneration)
        {
            // LEGACY BRIDGE, Lab/tests only: `valueUnits` is the retired integer unit
            // (1 unit = 4,100,000 mm^3). The shipped path is
            // SubmitCandidateWithVolumeKMm3 with the host's pre-priced growth; this bridge
            // exists so a lab scene (CpuSkirmish's PlaygroundProp) keeps its authored number
            // without re-importing the scene this round.
            long growthMm3 = LegacyUnitsToGrowthMm3(valueUnits);
            if (growthMm3 <= 0L) return false;
            return SubmitCandidateWithVolumeKMm3(absorbable, kind, size, Quaternion.identity,
                worldPosition, growthMm3, itemInstanceId, itemGeneration,
                PickupAbsorbMath.DefaultVolumeK);
        }

        /// <summary>One retired integer unit in mm^3 (4,100,000), rounded; -1 when illegal.</summary>
        /// <remarks>
        /// The old calibration was 1 unit = 0.0041 m^3 = 4,100,000 mm^3, so the conversion
        /// is exact for every integer unit. Kept in ONE place so the Lab bridge cannot drift
        /// from the history it bridges.
        /// </remarks>
        private static long LegacyUnitsToGrowthMm3(float valueUnits)
        {
            if (!KatamariMath.IsFiniteFloat(valueUnits) || valueUnits <= 0f) return -1L;
            float mm3 = valueUnits * 4100000f;
            if (!KatamariMath.IsFiniteFloat(mm3)) return -1L;
            long rounded = (long)Mathf.Round(mm3);
            return rounded > 0L ? rounded : -1L;
        }

        /// <summary>
        /// Queues one candidate whose growth is already priced in mm^3, at the default k.
        /// </summary>
        public bool SubmitCandidateMm3(bool absorbable, string kind, Vector3 size,
            Vector3 worldPosition, long growthMm3, string itemInstanceId, int itemGeneration)
        {
            return SubmitCandidateWithVolumeKMm3(absorbable, kind, size, Quaternion.identity,
                worldPosition, growthMm3, itemInstanceId, itemGeneration,
                PickupAbsorbMath.DefaultVolumeK);
        }

        /// <summary>
        /// The one queue writer: k for the gate, growth for the ledger, both per item.
        /// </summary>
        /// <remarks>
        /// <para>
        /// `volumeK` is the original's `Volume = k * bbox` factor for THIS item: the gate's
        /// Req depends on it (`Req = floor(1000*cbrt(60*k*a*b*c/pi))`), so two items with
        /// the same bounds but different k are not equally absorbable. `growthMm3` is the
        /// ledger gain the same item is worth (penalty * k * bbox, priced once). They travel
        /// together because they are the same underlying volume seen by two consumers.
        /// </para>
        /// <para>
        /// `size` is the item's own three edge lengths, the original's `a, b, c`: the gate
        /// multiplies them, so it never depends on how the item is turned. `rotation` is the
        /// item's world rotation at submission; with the position it places that box in the
        /// world for the contact test. A caller must never pass the world AABB of a turned
        /// item as `size`. The bridges above (lab props) pass the identity rotation.
        /// </para>
        /// <para>
        /// Kept as separate entry points rather than optional parameters because UdonSharp
        /// does not support default arguments, and because a lab that submits a hand-authored
        /// prop should not have to name a k it does not have.
        /// </para>
        /// </remarks>
        public bool SubmitCandidateWithVolumeKMm3(bool absorbable, string kind, Vector3 size,
            Quaternion rotation, Vector3 worldPosition, long growthMm3, string itemInstanceId,
            int itemGeneration, float volumeK)
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
            if (growthMm3 <= 0L) return false;

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
            pendingSizes[index] = size;
            pendingRotations[index] = rotation;
            pendingPositions[index] = worldPosition;
            pendingGrowthMm3[index] = growthMm3;
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
            return RemovePendingById(itemInstanceId, false);
        }

        /// <summary>
        /// <see cref="WithdrawCandidate"/> for a host that re-ranks its candidates while the
        /// ball is rolling: it leaves a candidate that this step's gate already accepted.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <c>pendingAccepted</c> is written by the gate stage (<see cref="EvaluatePendingCandidates"/>)
        /// and consumed by the commit stage (<see cref="CommitAcceptedAbsorbs"/>) of the SAME
        /// step; a successful commit removes the entry, so between steps a true flag survives
        /// only a commit that failed (no local ledger row, or the ledger refused the key), and
        /// the next gate stage rewrites it. An accepted entry is a pending claim, not a
        /// proximity guess: removing it would swallow the absorption the gate just granted, so
        /// a re-ranking host must not do it. A round-end release
        /// (<c>SetCollecting(false)</c>, a field reset) uses <see cref="WithdrawCandidate"/>
        /// instead, because there "nothing may absorb any more" outranks the pending claim.
        /// </para>
        /// <para>
        /// True when an entry was removed; false when the id is not queued OR is held because
        /// it is accepted - <see cref="IsCandidateQueued"/> tells the two apart.
        /// </para>
        /// </remarks>
        public bool WithdrawCandidateUnlessAccepted(string itemInstanceId)
        {
            return RemovePendingById(itemInstanceId, true);
        }

        /// <summary>True when an entry with this item id is in the pending queue.</summary>
        public bool IsCandidateQueued(string itemInstanceId)
        {
            if (itemInstanceId == null || itemInstanceId.Length == 0) return false;
            for (int i = 0; i < pendingCandidateCount; i++)
            {
                if (pendingItemIds[i] == itemInstanceId) return true;
            }
            return false;
        }

        /// <summary>
        /// Updates the world pose of a queued candidate that the gate has not accepted yet.
        /// </summary>
        /// <remarks>
        /// <para>
        /// For a host whose item moves while it waits in the queue: the contact test reads
        /// <c>pendingPositions</c>/<c>pendingRotations</c>, so a stale snapshot judges the
        /// item where it was at submission. Only those two arrays are written. The submit
        /// time, the accepted flag, the gate outcome, the ledger and the contact window are
        /// left alone - a refresh is not a re-submission, and an accepted entry is a pending
        /// claim whose verdict was reached at its old pose.
        /// </para>
        /// <para>
        /// No threshold here: whether a pose change is worth a refresh is the caller's call.
        /// True when the pose was written; false for an empty id, an id that is not queued,
        /// or an accepted entry. Size never changes, so it is not a parameter.
        /// </para>
        /// </remarks>
        public bool RefreshCandidatePoseUnlessAccepted(string itemInstanceId,
            Vector3 worldPosition, Quaternion rotation)
        {
            if (itemInstanceId == null || itemInstanceId.Length == 0) return false;
            for (int i = 0; i < pendingCandidateCount; i++)
            {
                if (pendingItemIds[i] != itemInstanceId) continue;
                if (pendingAccepted[i]) return false;
                pendingPositions[i] = worldPosition;
                pendingRotations[i] = rotation;
                return true;
            }
            return false;
        }

        // The one remover both public withdraws share. keepAccepted keeps an entry whose
        // gate verdict is Absorb and that has not committed yet (see the remarks above).
        private bool RemovePendingById(string itemInstanceId, bool keepAccepted)
        {
            if (itemInstanceId == null || itemInstanceId.Length == 0) return false;
            if (pendingCandidateCount <= 0) return false;

            int write = 0;
            bool removed = false;
            for (int read = 0; read < pendingCandidateCount; read++)
            {
                if (pendingItemIds[read] == itemInstanceId
                    && !(keepAccepted && pendingAccepted[read]))
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
        /// The plane is the <c>dot(normal, point) = offset</c> pair. It is separate from
        /// <see cref="AddPlane"/> on purpose: a boundary plane is a wall and never a
        /// support, and the ground is a contact source only where no terrain answers
        /// (design §3.4). The previous support height is re-derived from the ball's own
        /// bottom so the diagnostics do not keep the previous floor.
        /// </remarks>
        public bool SetGroundPlane(Vector3 normal, float offset)
        {
            if (!KatamariMath.IsFiniteVector(normal)) return false;
            if (!KatamariMath.IsFiniteFloat(offset)) return false;
            Vector3 unitNormal;
            float rawLength;
            if (!KatamariMath.TryNormalize(normal, out unitNormal, out rawLength)) return false;
            // A wall-like ground plane cannot answer "what is the height under the probe",
            // so it is refused here instead of failing every step inside the solve.
            if (unitNormal.y <= 1e-3f) return false;
            groundPlaneNormal = normal;
            groundPlaneOffset = offset;
            // The floor moved: the previous support height is about the old one.
            lastSupportHeightWorldM = ballCenterWorld.y - collisionRadiusWorldM;
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
            return true;
        }

        public bool AddBox(Vector3 minimum, Vector3 maximum)
        {
            // Same rule as AddPlane: the writer ensures the destination.
            if (!EnsureRegistrationBuffers()) return false;
            if (boxCount >= MaxBoxCount)
            {
                // Explicit failure, never a silent drop: the caller (LevelArenaHost) counts
                // the refusal and the importer reports [structure_capacity] for the level.
                SignalOverflow("box capacity " + MaxBoxCount + " exceeded");
                return false;
            }
            if (!KatamariMath.IsFiniteVector(minimum)) return false;
            if (!KatamariMath.IsFiniteVector(maximum)) return false;
            if (minimum.x > maximum.x || minimum.y > maximum.y ||
                minimum.z > maximum.z) return false;
            int boxIndex = boxCount;
            boxCount = boxCount + 1;
            // The box is registered as an axis-aligned OBB under surface id 1 + index.
            RegisterBoxObb(boxIndex, minimum, maximum);
            return true;
        }

        /// <summary>
        /// Registers one static ramp: an oriented walking plane plus a horizontal
        /// footprint, registered as a slab OBB.
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
        /// A ramp must never also be registered through <see cref="AddBox"/>: the same
        /// board would be two solids at one place (design §5.5). LevelArenaHost keeps the two arrays separate and reports a
        /// collider that appears in both.
        /// </para>
        /// <para>
        /// Only the count and the refusal state are kept; the board itself is the slab OBB.
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
            rampCount = rampCount + 1;
            // Ball contact v2: the ramp as a solid slab whose walking face is the same
            // plane (design §1.5).
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

        public void ClearObstacles()
        {
            planeCount = 0;
            boxCount = 0;
            // Ramps are scene geometry like the static boxes: "all obstacles" clears them
            // too, and the arena's Reapply re-registers them after this call.
            rampCount = 0;
            // The OBB tables are the scene geometry. Props go first, so ClearObbs does
            // not have to move a prop block down.
            ClearPropObbs();
            ClearObbs();
        }

        // ------------------------------------------------------------------
        // Terrain mesh (T0/T1; LEVEL_FORMAT_V2 §13)
        // ------------------------------------------------------------------

        /// <summary>
        /// Copies one baked terrain mesh into the motor's fixed buffers. The writer owns
        /// the source arrays; this call allocates the motor buffers once (writer-allocates
        /// rule), validates/copies them and computes the chunk AABBs. Chunks must be
        /// contiguous, cover every triangle once, and hold at most 512 triangles.
        /// </summary>
        public bool SetTerrainMesh(
            Vector3[] vertices, int vertexCount,
            int[] indices, int indexCount,
            int[] chunkStarts, int[] chunkTriangleCounts, int[] chunkZoneIds,
            int chunkCount)
        {
            EnsureTerrainMeshBuffers();
            terrainMeshReady = false;
            int triangles;
            int chunks;
            if (!TerrainMeshMath.TryBuildMesh(vertices, vertexCount, indices, indexCount,
                chunkStarts, chunkTriangleCounts, chunkZoneIds, chunkCount,
                terrainVertices, terrainIndices, terrainChunkStarts,
                terrainChunkTriangleCounts, terrainChunkZoneIds, terrainChunkMin,
                terrainChunkMax, out triangles, out chunks))
            {
                terrainVertexCount = 0;
                terrainTriangleCount = 0;
                terrainChunkCount = 0;
                return false;
            }

            terrainVertexCount = vertexCount;
            terrainTriangleCount = triangles;
            terrainChunkCount = chunks;
            terrainMeshReady = true;
            lastTerrainQueryMicros = 0f;
            BuildTerrainGrid(chunks);
            return true;
        }

        /// <summary>
        /// Builds the XZ grid and the per-triangle lookup data for the mesh just registered.
        /// </summary>
        /// <remarks>
        /// <para>
        /// A LOAD-TIME call (SetTerrainMesh), never in the frame. A grid refusal does NOT
        /// refuse the mesh: collision must not depend on an acceleration structure, so the
        /// motor keeps the mesh, records the refusal and the query falls back to the full
        /// chunk scan. The refusal is explicit (<see cref="GetTerrainGridRefusalCode"/> plus
        /// <see cref="GetTerrainGridRefusalReason"/>) and TerrainFieldHost logs it, so a
        /// silent performance cliff cannot happen.
        /// </para>
        /// <para>
        /// The cell size is the serialized <c>terrainGridCellM</c> clamped to the grid's
        /// range; the origin and cell counts come from the chunk AABBs' union, which is the
        /// mesh's true XZ extent.
        /// </para>
        /// </remarks>
        private void BuildTerrainGrid(int chunkCount)
        {
            terrainGridReady = false;
            terrainGridCellCount = 0;
            terrainGridRefusalCode = TerrainGridMath.RefusalNone;
            terrainGridRefusalDetail = 0;
            terrainCandidateCount = 0;
            if (terrainCandidateMark != null)
            {
                for (int i = 0; i < terrainCandidateMark.Length; i++) terrainCandidateMark[i] = false;
            }
            if (chunkCount <= 0 || terrainChunkMin == null || terrainChunkMax == null) return;

            float minX = float.MaxValue;
            float minZ = float.MaxValue;
            float maxX = -float.MaxValue;
            float maxZ = -float.MaxValue;
            for (int c = 0; c < chunkCount; c++)
            {
                Vector3 chunkLow = terrainChunkMin[c];
                Vector3 chunkHigh = terrainChunkMax[c];
                if (!KatamariMath.IsFiniteVector(chunkLow)) return;
                if (!KatamariMath.IsFiniteVector(chunkHigh)) return;
                if (chunkLow.x < minX) minX = chunkLow.x;
                if (chunkLow.z < minZ) minZ = chunkLow.z;
                if (chunkHigh.x > maxX) maxX = chunkHigh.x;
                if (chunkHigh.z > maxZ) maxZ = chunkHigh.z;
            }

            float cellM = terrainGridCellM;
            if (!KatamariMath.IsFiniteFloat(cellM) || cellM < TerrainGridMath.MinCellM)
                cellM = TerrainGridMath.DefaultCellM;
            if (cellM > TerrainGridMath.MaxCellM) cellM = TerrainGridMath.MaxCellM;

            int refusalCode;
            int refusalDetail;
            int cellsX;
            int cellsZ;
            int cellCount;
            int referenceCount;
            int oversizedCount;
            float originX;
            float originZ;
            if (!TerrainGridMath.TryBuild(minX, minZ, maxX, maxZ, cellM,
                terrainVertices, terrainIndices, terrainTriangleCount, terrainChunkStarts,
                terrainChunkTriangleCounts, terrainChunkZoneIds, chunkCount,
                terrainTriangleMinX, terrainTriangleMaxX, terrainTriangleMinZ,
                terrainTriangleMaxZ, terrainTriangleSurfaceId, terrainCellStart,
                terrainCellTriangles, terrainCellCursor, terrainOversizedTriangles,
                out originX, out originZ, out cellsX, out cellsZ, out cellCount,
                out referenceCount, out oversizedCount, out refusalCode, out refusalDetail))
            {
                // A refused grid leaves NO grid behind (the build zeroes its geometry outs):
                // the mesh keeps its collision through the full scan and the code names why.
                terrainGridRefusalCode = refusalCode;
                terrainGridRefusalDetail = refusalDetail;
                terrainGridReady = false;
                terrainGridCellCount = 0;
                terrainOversizedCount = 0;
                return;
            }

            terrainGridOriginX = originX;
            terrainGridOriginZ = originZ;
            terrainGridCellM = cellM;
            terrainGridCellsX = cellsX;
            terrainGridCellsZ = cellsZ;
            terrainGridCellCount = cellCount;
            terrainOversizedCount = oversizedCount;
            terrainGridReady = true;
        }

        /// <summary>
        /// Zeroes this step's terrain cost counters. Called from <see cref="StepBall"/> (both
        /// position authorities) and from <see cref="Update"/> for a frame with no step.
        /// </summary>
        private void ResetTerrainCostCounters()
        {
            lastTerrainQueryMicros = 0f;
            lastTerrainSupportMicros = 0f;
            terrainTrianglesTested = 0;
            terrainFullEvals = 0;
        }

        /// <summary>Drops the terrain mesh; the plane/box/ramp support world stays.</summary>
        public void ClearTerrainMesh()
        {
            terrainVertexCount = 0;
            terrainTriangleCount = 0;
            terrainChunkCount = 0;
            terrainMeshReady = false;
            terrainGridReady = false;
            terrainGridCellCount = 0;
            terrainGridRefusalCode = TerrainGridMath.RefusalNone;
            terrainGridRefusalDetail = 0;
            terrainCandidateCount = 0;
            if (terrainCandidateMark != null)
            {
                for (int i = 0; i < terrainCandidateMark.Length; i++) terrainCandidateMark[i] = false;
            }
            lastTerrainQueryMicros = 0f;
        }

        public bool IsTerrainMeshContactEnabled()
        {
            return terrainMeshContactEnabled;
        }

        /// <summary>Turns terrain mesh contact on or off at runtime (lab / EditMode).</summary>
        public void SetTerrainMeshContactEnabled(bool enabled)
        {
            terrainMeshContactEnabled = enabled;
        }

        public int GetTerrainMeshTriangleCount()
        {
            return terrainTriangleCount;
        }

        public int GetTerrainMeshChunkCount()
        {
            return terrainChunkCount;
        }

        /// <summary>
        /// The LAST fixed step's terrain query time in microseconds: the support selection
        /// plus the T1 push-out. <b>A frame's cost is this reading times the number of steps
        /// the frame ran</b> - see <see cref="GetStepsThisFrame"/>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Measured with the wall clock (<see cref="WallClockSeconds"/>), not with
        /// <c>Time.time</c>, which is a frame timestamp and made this counter read 0 on
        /// every step. It is a diagnostic: nothing branches on it, it allocates nothing, and
        /// its own cost is two property reads per query.
        /// </para>
        /// <para>
        /// Reset once per step in <see cref="StepBall"/>, so it reads the LAST step only.
        /// It is the support query's time (<see cref="GetLastTerrainSupportMicros"/>).
        /// </para>
        /// </remarks>
        public float GetLastTerrainQueryMicros()
        {
            return lastTerrainQueryMicros;
        }

        /// <summary>The last step's share of the terrain query spent selecting the support.</summary>
        public float GetLastTerrainSupportMicros()
        {
            return lastTerrainSupportMicros;
        }

        /// <summary>
        /// Triangles the last terrain query actually walked: the grid candidate list, or 0
        /// when no grid is available (the query then scans every chunk and
        /// <see cref="GetTerrainTrianglesTested"/> shows the full count).
        /// </summary>
        public int GetTerrainCandidates()
        {
            // -1 = no candidate list (road the full scan), 0 = an empty window.
            return terrainSupportCandidateCount;
        }

        /// <summary>
        /// Candidates the last v2 contact collection walked, over the sweep window
        /// (radius + half the step travel + contact skin). 0 when the v2 authority is off.
        /// </summary>
        public int GetTerrainContactCandidates()
        {
            // -1 = no candidate list (the contact collection ran the full scan).
            return terrainContactCandidateCount;
        }

        /// <summary>Triangles too big for the cells, offered to every query (0 without a grid).</summary>
        public int GetTerrainOversizedTriangles()
        {
            return terrainOversizedCount;
        }

        /// <summary>True when the terrain grid built and the candidate path is in use.</summary>
        public bool IsTerrainGridReady()
        {
            return terrainGridReady;
        }

        /// <summary>Cells in the terrain grid (0 when it was refused or no mesh is set).</summary>
        public int GetTerrainGridCellCount()
        {
            return terrainGridCellCount;
        }

        /// <summary>The cell size the grid was actually built with, metres.</summary>
        public float GetTerrainGridCellM()
        {
            return terrainGridCellM;
        }

        /// <summary>
        /// Why the grid was refused (0 = accepted). A refusal is a PERFORMANCE answer only:
        /// the mesh stays registered and the queries fall back to the full scan.
        /// </summary>
        public int GetTerrainGridRefusalCode()
        {
            return terrainGridRefusalCode;
        }

        /// <summary>The number that broke the cap, for a log line (cells / span / references).</summary>
        public int GetTerrainGridRefusalDetail()
        {
            return terrainGridRefusalDetail;
        }

        /// <summary>The one literal per grid refusal code; "" when the grid is fine.</summary>
        public string GetTerrainGridRefusalReason()
        {
            return TerrainGridMath.RefusalReason(terrainGridRefusalCode);
        }

        /// <summary>
        /// Triangles the last step's support query iterated: every triangle of every chunk
        /// whose AABB contains a probe (17 probes). Compare with
        /// <see cref="GetTerrainFullEvals"/>: a large <c>trianglesTested</c> means the chunk
        /// broad phase is not filtering; a large <c>fullEvals</c> share means the per-triangle
        /// work is what costs.
        /// </summary>
        public int GetTerrainTrianglesTested()
        {
            return terrainTrianglesTested;
        }

        /// <summary>
        /// Triangles that passed the per-triangle XZ AABB in the last support query and paid
        /// the cross product, plane height and barycentric test. This is the number to divide
        /// <see cref="GetLastTerrainSupportMicros"/> by to get "microseconds per full
        /// triangle evaluation".
        /// </summary>
        public int GetTerrainFullEvals()
        {
            return terrainFullEvals;
        }

        /// <summary>
        /// Fixed steps the last <c>Update</c> ran (0..<c>maxStepsPerFrame</c>). Multiply
        /// <see cref="GetLastTerrainQueryMicros"/> by this for the frame's terrain cost; a
        /// frame with no step reports 0 and zeroes the terrain readings with it, so the
        /// product is never misleadingly small.
        /// </summary>
        public int GetStepsThisFrame()
        {
            return stepsThisFrame;
        }

        private void EnsureTerrainMeshBuffers()
        {
            if (terrainVertices != null) return;
            terrainVertices = new Vector3[TerrainMeshMath.MaxVertices];
            terrainIndices = new int[TerrainMeshMath.MaxTriangles * 3];
            terrainChunkStarts = new int[TerrainMeshMath.MaxChunks];
            terrainChunkTriangleCounts = new int[TerrainMeshMath.MaxChunks];
            terrainChunkZoneIds = new int[TerrainMeshMath.MaxChunks];
            terrainChunkMin = new Vector3[TerrainMeshMath.MaxChunks];
            terrainChunkMax = new Vector3[TerrainMeshMath.MaxChunks];
            // Grid lookup data (load-time allocation, like every other terrain buffer):
            // 4 x 32 KB of per-triangle AABB floats, 32 KB of surface ids, the CSR table
            // (16 KB starts + 128 KB references + 16 KB cursor) and the per-step candidate
            // list + dedupe marks (32 KB + 8 KB). About 370 KB at the 8,192-triangle cap.
            terrainTriangleMinX = new float[TerrainMeshMath.MaxTriangles];
            terrainTriangleMaxX = new float[TerrainMeshMath.MaxTriangles];
            terrainTriangleMinZ = new float[TerrainMeshMath.MaxTriangles];
            terrainTriangleMaxZ = new float[TerrainMeshMath.MaxTriangles];
            terrainTriangleSurfaceId = new int[TerrainMeshMath.MaxTriangles];
            terrainCellStart = new int[TerrainGridMath.MaxGridCells + 1];
            terrainCellTriangles = new int[TerrainGridMath.MaxGridReferences];
            terrainCellCursor = new int[TerrainGridMath.MaxGridCells];
            terrainCandidates = new int[TerrainMeshMath.MaxTriangles];
            terrainCandidateMark = new bool[TerrainMeshMath.MaxTriangles];
            terrainOversizedTriangles = new int[TerrainGridMath.MaxOversizedTriangles];
            terrainVertexCount = 0;
            terrainTriangleCount = 0;
            terrainChunkCount = 0;
            terrainMeshReady = false;
            terrainGridReady = false;
            terrainGridCellCount = 0;
            terrainOversizedCount = 0;
            terrainCandidateCount = 0;
            terrainSupportCandidateCount = 0;
            terrainContactCandidateCount = 0;
            lastTerrainQueryMicros = 0f;
        }

        // ------------------------------------------------------------------
        // Static OBB registration (design §1.3/§1.4/§5.2). AddBox and AddRamp register
        // their solid as an OBB; AddObb registers a true OBB directly.
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
            staticObbEpoch = staticObbEpoch + 1;
            windowPreviousCount = 0;
            nearObbCount = 0;
            windowDroppedCount = 0;
            contactIncomplete = false;
        }

        /// <summary>
        /// Removes ONE static OBB: the entry with this surface id whose centre is at
        /// <paramref name="center"/>. Returns false (and changes nothing) when there is none.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The use is a prop that was rolled up while it had a same-id static solid (the
        /// reference level's big scenery): the prop leaves, so its collision must leave with
        /// it. The surface id alone is not trusted as a key - two arena hosts that were never
        /// given different offsets mint the same ids - so the baked centre is part of the key.
        /// </para>
        /// <para>
        /// The table layout invariant ("static prefix, then the prop block, no gaps") is kept
        /// by shifting every later static entry AND the whole prop block (OBB or proxy view)
        /// down by one slot, so the prop block's counts, its slots' surface ids (100 + block
        /// slot) and the proxy columns stay exactly as they were; the vacated slot is reset.
        /// The static counters and the refusal counters are untouched, the near set and its
        /// hysteresis are dropped (the next step rebuilds them), and <see cref="GetStaticObbEpoch"/>
        /// does NOT change (the table is the same one, one entry shorter).
        /// </para>
        /// </remarks>
        public bool RemoveStaticObb(int surfaceId, Vector3 center)
        {
            if (staticObbCenter == null) return false;
            int found = -1;
            for (int i = 0; i < staticObbCount; i++)
            {
                if (staticObbSurfaceId[i] != surfaceId) continue;
                Vector3 at = staticObbCenter[i];
                if (Mathf.Abs(at.x - center.x) > 1e-4f) continue;
                if (Mathf.Abs(at.y - center.y) > 1e-4f) continue;
                if (Mathf.Abs(at.z - center.z) > 1e-4f) continue;
                found = i;
                break;
            }
            if (found < 0) return false;

            int blockLength = propProxyCount > propObbCount ? propProxyCount : propObbCount;
            int end = staticObbCount + blockLength;
            for (int slot = found + 1; slot < end; slot++)
            {
                CopyObbSlot(slot, slot - 1);
                CopyProxySlot(slot, slot - 1);
            }
            // The last slot of the logical table is now a duplicate of its neighbour: reset
            // it so a stale proxy kind cannot be read as a live entry later.
            int vacated = end - 1;
            staticObbSurfaceId[vacated] = 0;
            staticObbRole[vacated] = 0;
            staticObbBoundingRadius[vacated] = 0f;
            if (staticProxyKind != null && vacated < staticProxyKind.Length)
            {
                staticProxyKind[vacated] = ProxyContactMath.KindBox;
                staticProxyRadius[vacated] = 0f;
                staticProxyHalfHeight[vacated] = 0f;
                staticProxyAxis[vacated] = ProxyContactMath.AxisX;
                staticProxyHullStart[vacated] = 0;
                staticProxyHullCount[vacated] = 0;
                staticProxyFaceStart[vacated] = 0;
                staticProxyFaceCount[vacated] = 0;
            }
            staticObbCount = staticObbCount - 1;
            // The near set holds COPIES of the entries it selected, and FillNearWindow returns
            // early when the new selection is empty and the old one was forgotten: without
            // this the removed solid would keep blocking from the stale copy until the ball
            // moved far enough to rebuild the window.
            nearObbCount = 0;
            windowPreviousCount = 0;
            return true;
        }

        /// <summary>
        /// Counts how many times the whole static table has been dropped (ClearObbs, hence
        /// ClearObstacles). A per-solid remove or restore is only meaningful while this still
        /// reads what the registering host saw.
        /// </summary>
        public int GetStaticObbEpoch()
        {
            return staticObbEpoch;
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
        /// a prop contact as a prop. Static boxes stay 1..64, ramps 500..503, the ground 0 and
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
            // behind.
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

        /// <summary>Turns collider-proxy contact on or off at runtime (lab / EditMode test).</summary>
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

        // The proxy columns of a slot. They are only meaningful for the prop block (a static
        // entry keeps the default box kind), and the hull/face tables they index are
        // block-local, so moving a slot never has to touch those tables.
        private void CopyProxySlot(int from, int to)
        {
            if (staticProxyKind == null) return;
            if (from == to) return;
            staticProxyKind[to] = staticProxyKind[from];
            staticProxyRadius[to] = staticProxyRadius[from];
            staticProxyHalfHeight[to] = staticProxyHalfHeight[from];
            staticProxyAxis[to] = staticProxyAxis[from];
            staticProxyHullStart[to] = staticProxyHullStart[from];
            staticProxyHullCount[to] = staticProxyHullCount[from];
            staticProxyFaceStart[to] = staticProxyFaceStart[from];
            staticProxyFaceCount[to] = staticProxyFaceCount[from];
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

        /// <summary>Copies world-space line pairs from the registered contact tables. Debug only.</summary>
        public int CopyCollisionWire(int layer, Vector3 near, float range, Vector3[] output)
        {
            if (output == null || output.Length < 6) return 0;
            int used = 0;
            if (layer == 1)
            {
                if (terrainVertices == null || terrainIndices == null) return 0;
                for (int chunk = 0; chunk < terrainChunkCount && used + 6 <= output.Length; chunk++)
                {
                    Vector3 lo = terrainChunkMin[chunk];
                    Vector3 hi = terrainChunkMax[chunk];
                    if (!CollisionWireMath.OverlapsWindow(lo, hi, near, range)) continue;
                    int end = terrainChunkStarts[chunk] + terrainChunkTriangleCounts[chunk];
                    for (int tri = terrainChunkStarts[chunk]; tri < end && used + 6 <= output.Length; tri++)
                    {
                        int corner = tri * 3;
                        Vector3 a = terrainVertices[terrainIndices[corner]];
                        Vector3 b = terrainVertices[terrainIndices[corner + 1]];
                        Vector3 c = terrainVertices[terrainIndices[corner + 2]];
                        if (!CollisionWireMath.TriangleInWindow(a, b, c, near, range)) continue;
                        used = CollisionWireMath.AppendTriangle(output, used, a, b, c);
                    }
                }
                return used;
            }
            if ((layer != 0 && layer != 2) || staticObbCenter == null) return 0;
            bool props = layer == 2;
            bool proxies = props && IsPropProxyContactEnabled() && propProxyCount > 0;
            int start = props ? staticObbCount : 0;
            int endBox = props ? start + (proxies ? propProxyCount : propObbCount) : staticObbCount;
            for (int i = start; i < endBox; i++)
            {
                Vector3 center = staticObbCenter[i];
                float reach = range + staticObbBoundingRadius[i];
                if ((center - near).sqrMagnitude > reach * reach) continue;
                int kind = proxies ? staticProxyKind[i] : ProxyContactMath.KindBox;
                if (kind == ProxyContactMath.KindConvex)
                {
                    int faceStart = staticProxyFaceStart[i];
                    int faceEnd = faceStart + staticProxyFaceCount[i];
                    int hullStart = staticProxyHullStart[i];
                    for (int f = faceStart; f < faceEnd && used + 6 <= output.Length; f++)
                        used = CollisionWireMath.AppendTriangle(output, used,
                            propProxyHullPoints[hullStart + propProxyFaceA[f]],
                            propProxyHullPoints[hullStart + propProxyFaceB[f]],
                            propProxyHullPoints[hullStart + propProxyFaceC[f]]);
                }
                else if (kind == ProxyContactMath.KindBox)
                {
                    used = CollisionWireMath.AppendBox(output, used, center, staticObbAxisX[i],
                        staticObbAxisY[i], staticObbAxisZ[i], staticObbHalfExtents[i]);
                }
                else
                {
                    used = CollisionWireMath.AppendRound(output, used, center, staticObbAxisX[i],
                        staticObbAxisY[i], staticObbAxisZ[i], staticProxyRadius[i],
                        kind == ProxyContactMath.KindCapsule ? staticProxyHalfHeight[i] : 0f,
                        staticProxyAxis[i]);
                }
            }
            return used;
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
        /// Registers a static box as an axis-aligned OBB under surface id 1 + index
        /// (design §1.4).
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
        /// Registers a ramp as a solid slab OBB (design §1.5): the walking face is the
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
        /// separate prop branch in the solver): 0 ground, 1..64 static box, 500..503 ramp,
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

        /// <summary>Cap lock: clears on release or after falling below half the height cap.</summary>
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
        /// Wall-clock cost of the last v2 step, microseconds.
        /// </summary>
        /// <remarks>
        /// Measured like <see cref="GetLastTerrainQueryMicros"/>: with
        /// <see cref="WallClockSeconds"/>, the one clock that advances during a frame. It
        /// used <c>Time.time</c> and therefore reported 0 for every fixed step inside one
        /// frame. It is still a per-step measurement now, but its resolution is the platform
        /// clock's, not a nanosecond counter: use it for an order of magnitude.
        /// </remarks>
        public float GetLastStepMicros()
        {
            return lastStepMicros;
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

        /// <summary>r_phys: the contact radius (design §4A.5, the collision radius).</summary>
        public float GetPhysicalRadiusWorldM()
        {
            return collisionRadiusWorldM;
        }

        /// <summary>
        /// The level's start diameter, metres: the size model's semantic diameter at zero
        /// confirmed volume (<c>TryDerive(startVolumeMm3, 0)</c>), so it is the number the
        /// HUD shows when the round begins. Constant for the life of the level: absorbs,
        /// banks and round resets never change it. 0 before Start has run.
        /// </summary>
        public float GetStartDiameterWorldM()
        {
            return startSemanticDiameterWorldM;
        }

        /// <summary>
        /// The drawn core's current radius equals the collision sphere radius used by the
        /// pickup contact gate. It follows size changes without smoothing; 0 before sizing.
        /// </summary>
        public float GetVisualCoreRadiusWorldM()
        {
            return collisionRadiusWorldM;
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
        /// The pickup sphere's radius, metres, independent of heading and rolling pins.
        /// The contact margin is reported separately by GetContactRadiusWorldM.
        /// </summary>
        public float GetPickupReachWorldM()
        {
            return collisionRadiusWorldM;
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
            long startEffectiveConfirmedMm3;
            if (!SizeModelMath.TryDerive(startVolumeMm3, 0L, maxBallDiameterM,
                collisionToCoreRadiusRatio, semanticDiameterPerCoreDiameter,
                out startCoreRadius, out startCollisionRadius, out startDiameter,
                out startEffectiveConfirmedMm3))
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

            long postBankConfirmedMm3;
            if (!BankShrinkMath.TryBankVolumeMm3(postBankDiameter, startVolumeMm3,
                semanticDiameterPerCoreDiameter, out postBankConfirmedMm3))
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
            if (!ApplyBankShrink(postBankConfirmedMm3)) { reason = 8; return false; }
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
        private bool ApplyBankShrink(long postBankConfirmedMm3)
        {
            long carried;
            if (!BankShrinkMath.TryCarriedVolumeMm3(postBankConfirmedMm3,
                unbankedVolumeUnits, out carried))
            {
                return false;
            }
            carriedVolumeMm3 = carried;
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
            rampRestTimerSeconds = 0f;
            rampSliding = false;
            previousStepRejected = false;
            contactHasPreviousFrame = false;
            contactPreviousCenterWorld = center;
            roundStartCenter = center;
            hasRoundStartCenter = true;
            sizeApplyDeferred = false;
            contactRadiusUsedWorldM = collisionRadiusWorldM;
            // The prop block was selected for the old position; the next host scan
            // re-registers it within one scan interval.
            ClearPropObbs();
            // A teleport can cross the whole map: the old support height and fall state are
            // about where the ball used to be. The next step re-derives them from the new
            // position, so a teleport into the air becomes a real fall.
            lastSupportHeightWorldM = center.y - collisionRadiusWorldM;
            isFalling = false;
            fallSpeedMps = 0f;
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
        /// Registers one absorbed prop's box into the ball's 16 pins (the radial proxy).
        /// </summary>
        /// <returns>
        /// True when the prop is represented (or was already registered); false when it
        /// was skipped. A skip is normal for a prop whose box does not clear the core by
        /// <c>minProxyProtrusionM</c> (it cannot lengthen any pin); capacity and
        /// invalid-input skips are refusals and are counted/logged. The reason of the most
        /// recent skip is <see cref="GetLastProxySkipReason"/>.
        /// </returns>
        /// <remarks>
        /// <para>
        /// bounds is the prop's full edge lengths in metres along its own axes (the host's
        /// prop-own size, the same one the absorb gate reads), read as the prop's local
        /// axes. ballLocalCenter and ballLocalRotation are the pose
        /// <c>AttachmentMath.TryWorldPoseToLocal</c> just produced for the visual
        /// attach, so the proxy and the visible prop occupy the same ball-frame box.
        /// Endpoints are the eight corners. EVERY attached prop registers, not only long
        /// ones: each pin's support length is the max, over all registered props, of the
        /// box's reach along the pin direction (the box support function, exact on the
        /// corners), which is the original's rolling model (16 pins that absorbed props
        /// lengthen). Registration is incremental: one attach dots 8 corners against the 16
        /// directions and max-merges, about 128 products and no rescan of earlier props.
        /// The box is centred on the prop's pivot (<c>ballLocalCenter</c>); a prop whose
        /// collider is offset from its pivot is approximated by that pivot-centred box.
        /// </para>
        /// <para>
        /// Idempotent by item id: one call per attached prop, and a retried call cannot
        /// add the same corners twice. The pin cap (<c>maxProxyProtrusionPerCoreRadius</c>,
        /// <c>maxProxyProtrusionWorldM</c>) is applied when the pins are read, not here, so
        /// the table keeps raw lengths and a growing ball needs no table write. Registered
        /// items are removed by a field reset,
        /// by <see cref="DetachRadialProxy"/>, or by <see cref="TryResetForNewRound"/>.
        /// </para>
        /// </remarks>
        public bool AttachRadialProxy(string itemInstanceId, Vector3 bounds,
            Vector3 ballLocalCenter, Quaternion ballLocalRotation)
        {
            int skipReason;
            bool attached = AttachmentStepMath.TryAttachRadialProxy(itemInstanceId, bounds,
                ballLocalCenter, ballLocalRotation, coreRadiusWorldM, minProxyProtrusionM,
                proxyDirections, proxySupports, proxyEndpointPool, ref proxyEndpointCount,
                proxyItemStarts, proxyItemLengths, proxyItemKeys, proxyItemIsCandidate,
                ref proxyItemCount, proxyCandidateKeys, ref proxyCandidateCount,
                proxyAffectedIndices, proxySolveScratch, proxyCorners, out skipReason);
            if (!attached) RecordProxySkip(skipReason);
            return attached;
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
            if (reason == ProxySkipInsideCore) return;
            proxyRefusalCount = proxyRefusalCount + 1;
            if (proxyRefusalLogCount >= 3) return;
            proxyRefusalLogCount = proxyRefusalLogCount + 1;
            Debug.LogWarning("[BallMotor] radial proxy refused: " + ProxySkipText(reason)
                + " (refusals " + proxyRefusalCount + ")");
        }

        private string ProxySkipText(int reason)
        {
            if (reason == ProxySkipInsideCore) return "no meaningful protrusion";
            if (reason == ProxySkipPoolFull) return "prop ledger full (" + ProxyItemCapacity + ")";
            if (reason == ProxySkipInvalidInput) return "invalid item input";
            if (reason == ProxySkipShellRefused) return "shell math refused the ledger";
            return "none";
        }

        /// <summary>Endpoints (8 corners per prop) currently in the shell ledger.</summary>
        public int GetProxyPointCount()
        {
            return proxyEndpointCount;
        }

        /// <summary>
        /// Props currently registered into the pins (0..ProxyItemCapacity): the number to
        /// compare with the attached-prop count, the difference being props whose box did
        /// not clear the core (<see cref="GetProxySkipCount"/>).
        /// </summary>
        public int GetProxyItemCount()
        {
            return proxyItemCount;
        }

        /// <summary>
        /// Pins that protrude past the core right now (0..16): the pins the contact pass
        /// sweeps. Valid after a step.
        /// </summary>
        public int GetProxyProtrudingPinCount()
        {
            if (proxySupports == null) return 0;
            int count = 0;
            for (int i = 0; i < RadialProxyMath.DirectionCount; i++)
            {
                if (proxySupports[i] > coreRadiusWorldM + ContactTuning.NeedleProtrusionThresholdM)
                    count = count + 1;
            }
            return count;
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
        /// 500+i = static ramp i (P3), 100+i = support-prop slot i (P2), 2000+ = terrain.
        /// The host
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
        /// 2 = static ramp (P3), 3 = support prop top (P2), 4 = climbing contact (v2),
        /// 5 = needle only (v2), 6 = terrain mesh triangle, -1 = no surface resolved.
        /// </summary>
        /// <remarks>
        /// It is a kind, not an index: <see cref="GetSupportSurfaceId"/> names WHICH box,
        /// ramp or prop, this says WHAT it is, and a reader that only needs "is this a
        /// slope" should read this one. Ids are ordered so the range tests are the ones
        /// SupportSurfaceMath already owns: ramps are 500..503 (FirstRampSurfaceId + i),
        /// props are 100..105 (FirstPropSurfaceId + i, a bounded range) and the terrain is
        /// 2000+, while static boxes stay 1..64.
        /// </remarks>
        public int GetSupportKind()
        {
            // 5 = only a pin is holding the ball up, and 4 = the ball is on a climbing
            // contact (a wall with no floor) (design §2.5).
            if (supportFromNeedle) return 5;
            if (supportFromClimb) return 4;
            if (supportSurfaceId == SupportSurfaceMath.GroundSurfaceId) return 0;
            if (SupportSurfaceMath.IsRampSurfaceId(supportSurfaceId)) return 2;
            if (SupportSurfaceMath.IsPropSurfaceId(supportSurfaceId)) return 3;
            if (TerrainMeshMath.IsTerrainSurfaceId(supportSurfaceId)) return 6;
            // Static boxes are 1 + i, i < MaxBoxCount, and every other range is excluded
            // above, so the remainder is a box id.
            if (supportSurfaceId > 0) return 1;
            return -1;
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
        /// The ledger's CONFIRMED GROWTH for the local row, exactly, in mm^3.
        /// </summary>
        /// <remarks>
        /// MEANING, chosen once and used everywhere: this is the volume the ledger has
        /// confirmed ABOVE the start ball - unbanked commits plus the already-scored carried
        /// residual - NOT the ball's total volume and NOT the start volume. At round start it
        /// is 0. The total is <see cref="GetTotalVolumeMm3"/>; a scene that wants "how big is
        /// the ball" must not read this one (the LabBallSizeSetter adds the start explicitly,
        /// which is why <c>sizeModelStartVolumeMm3</c> travels with the setter).
        /// </remarks>
        public long GetConfirmedVolumeMm3()
        {
            long confirmed = unbankedVolumeUnits + carriedVolumeMm3;
            return confirmed < 0L ? 0L : confirmed;
        }

        /// <summary>The ball's TOTAL volume in mm^3: start ball + confirmed growth.</summary>
        /// <remarks>
        /// The quantity the size model and the gate actually use. Integer addition only; it is
        /// never accumulated in float.
        /// </remarks>
        public long GetTotalVolumeMm3()
        {
            long total = startVolumeMm3 + unbankedVolumeUnits + carriedVolumeMm3;
            return total < 0L ? 0L : total;
        }

        /// <summary>The confirmed growth in cubic metres - a display/analysis reading only.</summary>
        public float GetConfirmedVolumeCubicMetres()
        {
            return (float)GetConfirmedVolumeMm3() * 1e-9f;
        }

        /// <summary>The ball's total volume in cubic metres - a display/analysis reading only.</summary>
        public float GetTotalVolumeCubicMetres()
        {
            return (float)GetTotalVolumeMm3() * 1e-9f;
        }

        /// <summary>
        /// The HUD diameter: the semantic diameter plus the original's +0.02 cm (parity G09).
        /// </summary>
        /// <remarks>
        /// A DISPLAY reading only. The gate and the size model use the un-offset semantic
        /// diameter, so the fudge can never move a threshold.
        /// </remarks>
        public float GetHudDiameterWorldM()
        {
            return SizeModelMath.HudDiameterWorldM(semanticDiameterWorldM);
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
        /// True once any fixed-capacity registration was refused.
        /// </summary>
        public bool HasOverflowSignal()
        {
            return overflowLatched;
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
        /// </summary>
        /// <remarks>
        /// Written once, so it names a capacity rather than growing into a
        /// running log.
        /// </remarks>
        public string GetFirstOverflowReason()
        {
            return firstOverflowReason;
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
        private void RecordAbsorbedEvent(int pendingIndex, Vector3 contactCenter,
            Vector3 supportNormal)
        {
            int eventIndex = absorbedEventCount;
            AttachmentStepMath.RecordAbsorbedEvent(pendingItemIds[pendingIndex],
                pendingPositions[pendingIndex], pendingRotations[pendingIndex], ballCenterWorld,
                visualRollRotation, contactCenter, supportNormal, coreRadiusWorldM,
                collisionRadiusWorldM, contactRadiusWorldM, discontinuous, maxRollDisplacementM,
                authorityFrame, absorbedEventItemIds, absorbedEventPositions,
                absorbedEventLocalPositions, absorbedEventLocalRotations, absorbedEventBallCenters,
                absorbedEventCollisionRadii, absorbedEventContactRadii, absorbedEventSteps,
                absorbedEventPoseValid, ref absorbedEventCount, ref absorbedEventRingCount);
            if (absorbedEventItemIds != null) LogAbsorbGapOnce(pendingIndex, contactCenter, eventIndex);
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

        /// <summary>
        /// Reads one retained contact-frame payload atomically. Invalid/evicted events
        /// fail closed: no caller may fall back to the ball's current pose.
        /// </summary>
        public bool TryGetAbsorbedEventAttachment(int absoluteIndex, out Vector3 localPosition,
            out Quaternion localRotation, out Vector3 contactCenter, out float collisionRadius,
            out float contactRadius, out int delaySteps)
        {
            return AttachmentStepMath.TryReadAbsorbedEventAttachment(absoluteIndex, authorityFrame,
                absorbedEventItemIds, absorbedEventLocalPositions, absorbedEventLocalRotations,
                absorbedEventBallCenters, absorbedEventCollisionRadii, absorbedEventContactRadii,
                absorbedEventSteps, absorbedEventPoseValid, absorbedEventCount, absorbedEventRingCount,
                out localPosition, out localRotation, out contactCenter, out collisionRadius,
                out contactRadius, out delaySteps);
        }

        public Vector3 GetAbsorbedEventPosition(int absoluteIndex)
        {
            int slot = AbsorbedEventSlot(absoluteIndex);
            if (slot < 0) return Vector3.zero;
            return absorbedEventPositions[slot];
        }

        /// <summary>
        /// Prints, for the first <c>AbsorbGapLogLimit</c> committed events, the item's
        /// signed gap to the accepting step's collision sphere plus contact margin.
        /// </summary>
        /// <remarks>
        /// <c>boxGapMm = (boxDistance - collisionRadius - contactRadius) * 1000</c>.
        /// The shared AttachmentMath adapter evaluates the gate's geometry. A committed
        /// event must report a non-positive gap; the host measures the frozen local pose
        /// against these same captured radii and pairs it by event index. Rate-limited
        /// for the whole run so a long round cannot flood the console.
        /// </remarks>
        private void LogAbsorbGapOnce(int index, Vector3 contactCenter, int eventIndex)
        {
            if (absorbGapLogged >= AbsorbGapLogLimit) return;
            float gap;
            if (!AttachmentMath.TryComputeBoxGap(pendingSizes[index], pendingRotations[index],
                pendingPositions[index], contactCenter, collisionRadiusWorldM,
                contactRadiusWorldM, out gap)) return;
            absorbGapLogged = absorbGapLogged + 1;
            Debug.Log("[BallMotor] absorb-gap#" + absorbGapLogged
                + " id=" + pendingItemIds[index]
                + " event=" + eventIndex + " absorbStep=" + authorityFrame
                + " boxGapMm=" + (gap * 1000f).ToString("F1")
                + " contactMm=" + (contactRadiusWorldM * 1000f).ToString("F1")
                + " ballR=" + collisionRadiusWorldM);
        }

        /// <summary>
        /// Prints the full input set of the first few refusals, once each.
        /// </summary>
        /// <remarks>
        /// The ring says which branch refused; this says with what numbers, so a
        /// a wrong contact limit or a wrong item size is visible without reading coordinates
        /// by hand. Capped at <c>rejectDiagnosticsLimit</c> lines for the whole run so
        /// a persistent refusal cannot flood the console.
        /// </remarks>
        private void LogRejectDiagnosticsOnce(int index, AbsorbOutcome outcome, Vector3 center)
        {
            if (rejectDiagnosticsLogged >= rejectDiagnosticsLimit) return;
            rejectDiagnosticsLogged = rejectDiagnosticsLogged + 1;

            float boxDistance;
            float contactLimit;
            if (!PickupAbsorbMath.TryComputeContact(pendingSizes[index], pendingRotations[index],
                pendingPositions[index], center, collisionRadiusWorldM, contactRadiusWorldM,
                out boxDistance, out contactLimit))
            {
                // Illegal geometry: the gate refused it as invalid input before it had a
                // distance, so there is no number to print. -1 is this file's invalid marker.
                boxDistance = -1f;
                contactLimit = -1f;
            }

            Debug.Log("[BallMotor] reject#" + rejectDiagnosticsLogged
                + " id=" + pendingItemIds[index]
                + " kind=" + pendingKinds[index]
                + " outcome=" + (int)outcome
                + " d=" + Vector3.Distance(pendingPositions[index], center)
                + " boxDist=" + boxDistance
                + " limit=" + contactLimit
                + " size=" + pendingSizes[index]
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
        public long GetPendingCandidateGrowthMm3(int index)
        {
            if (pendingGrowthMm3 == null) return -1L;
            if (index < 0 || index >= pendingCandidateCount) return -1L;
            return pendingGrowthMm3[index];
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
        /// commitRow -1 marks the "success but no commit row" case; claimCountAtFailure
        /// is the claim count when the refused row failed.
        /// </remarks>
        private void LogCommitFailureOnce(string itemId, long gain, int claimFailure,
            int resolveFailure, int commitRow, int claimCountAtFailure)
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
                + " claimCount=" + claimCountAtFailure);
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
            return AttachmentStepMath.AbsorbedEventSlot(absoluteIndex, absorbedEventItemIds,
                absorbedEventCount, absorbedEventRingCount);
        }
    }
}
