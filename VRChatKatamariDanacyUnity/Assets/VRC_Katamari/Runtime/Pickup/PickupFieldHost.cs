using UdonSharp;
using UnityEngine;
using VRCKatamari.BallContact;
using VRCKatamari.BallLayers;
using VRCKatamari.BallMotor;
using VRCKatamari.Core;
using VRCKatamari.Diagnostics;
using VRCKatamari.Story;

namespace VRCKatamari.Pickup
{
    /// <summary>
    /// The pickup host for one field of props: it queues the props near the ball as
    /// candidates, then parents every prop the motor reports as absorbed to the ball's
    /// attachment root, so an eaten prop stays visible and rides with the ball instead of
    /// disappearing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Why one host and not one component per prop. The pickup layer's contract leaves
    /// candidate collection to the host, and the motor's queue holds four candidates.
    /// The project's prop budget is hundreds of items per field (the PicnicLawn greybox
    /// alone places 448), so a per-prop <c>Update</c> would put hundreds of scripts on the
    /// frame for no benefit, and a per-prop attach scan would make every one of them walk
    /// the motor's event ring. This behaviour therefore owns one serialized parallel-array
    /// row per prop and one scan.
    /// </para>
    /// <para>
    /// Coordinates. Positions are world metres in the project's single left-handed space
    /// (+Y up). An attached prop's local pose is relative to <c>attachmentRoot</c>, which
    /// the scene places under the ball's visual roll root with identity local position and
    /// scale 1: with that placement the root's world pose IS the ball's rigid pose
    /// (ball centre + visual roll rotation), which is exactly the frame
    /// <see cref="AttachmentMath.TryWorldPoseToLocal"/> converts into. The host never
    /// scales the root or the prop: absorbed props keep their own scale and do not grow
    /// with the core (Docs/02_TECHNICAL_CONTRACTS.md section 2).
    /// </para>
    /// <para>
    /// The pose comes from the event's FROZEN contact-local payload. The motor records
    /// the same candidate world pose the gate judged, relative to that accepting step's
    /// ball centre and rolling rotation. Recomputing from current world transforms here
    /// would bake later steps or a same-step growth push-out into the attachment offset.
    /// </para>
    /// <para>
    /// Queue etiquette. The motor never removes a queued candidate except on a successful
    /// commit or a host withdraw, and its four slots refuse what they cannot hold, so the
    /// queue must hold only what the ball can take now, and always the nearest of those.
    /// Every scan therefore picks, among the free props inside <c>activationRadiusM</c>
    /// that pass the absorb gate's size test at the ball's CURRENT diameter, the nearest
    /// few (<see cref="PickupCandidateSelection"/>), withdraws the queued props of this
    /// host that are no longer in that selection (a farther prop displaced by a nearer one,
    /// a prop the ball rolled away from, one the size test no longer passes) and then
    /// queues the selected ones. A candidate the gate already accepted but the commit has
    /// not yet taken is never withdrawn (<c>WithdrawCandidateUnlessAccepted</c>). The host
    /// keeps a short list of the ids it queued, only so it knows what to withdraw: the
    /// motor stays the owner of "is this queued" (submitting twice is a no-op, and an id
    /// the motor no longer holds simply falls out of the list).
    /// </para>
    /// <para>
    /// What the ball draws (<see cref="AttachedDisplayMath"/>). An attached prop that lies
    /// wholly inside the original's bury sphere is buried for the rest of the round; of the
    /// rest only the biggest <c>displayBiggestCap</c> plus the most recent
    /// <c>displayRecentCap</c> keep their renderers on. Both only switch
    /// <c>Renderer.enabled</c> (back to the flag recorded when the prop attached); parent,
    /// pose, ledger, volume and the needle shell do not change, and a field reset turns
    /// every recorded renderer back to that flag.
    /// </para>
    /// <para>
    /// Attach or hide, never both. A prop whose pose the math refuses is hidden
    /// (<c>SetActive(false)</c>) and logged once with a reason code, because a prop that
    /// was absorbed but cannot be attached must not keep floating in the world. The two
    /// counts are separate: only attached props count toward the per-kind tally the story
    /// level's category goal will read.
    /// </para>
    /// <para>
    /// Prop obstacles (P2). A prop the ball cannot absorb yet must stop the ball instead of
    /// letting it roll through, and the ball's only collision authority is the motor's
    /// contact solver, so this host registers the nearby absorb-proof props as the motor's
    /// prop OBBs. A prop whose top is within the motor's step limit is instead a step
    /// and a floor (role support); both roles come from
    /// ONE classification of the same scan, so a prop is never a wall and a floor at once.
    /// The predicate is the absorb gate's OWN whole-millimetre rule
    /// (<see cref="PickupAbsorbMath.IsBeyondAbsorbLimit"/> with the prop's
    /// <c>propVolumeK</c>, the prop's own size and the motor's semantic diameter), never a
    /// second threshold: the instant the ball's whole-mm diameter reaches the prop's Req,
    /// the same function stops calling it "too big", the next scan drops it from both sets,
    /// and the prop becomes absorbable. Boxes come from the
    /// gameplay volume <c>propBounds</c> around the prop's world position - not from a
    /// <c>BoxCollider</c> and not from the visual mesh - because the three layers are
    /// separate and most scene props are authored without a collider at all
    /// (Docs/Design/PickupAbsorb/COLLIDER_PROXY_RUNTIME.md §4.1). Absorbed props are never
    /// candidates, and a field reset drops both sets.
    /// </para>
    /// <para>
    /// One owner. Each prop set is a single slot on the motor, so only ONE field host may
    /// write it per motor; two hosts would overwrite each other's work (last writer wins)
    /// and each would believe its own set was registered. There is no runtime arbiter - a
    /// second writer is a scene wiring error, checked in the editor.
    /// </para>
    /// <para>
    /// Assembly: one UdonSharpBehaviour per GameObject, so this belongs on its own object,
    /// not on the motor's. It writes no ball state; <c>motor</c>, <c>attachmentRoot</c> and
    /// the prop rows are the whole contract. UdonSharp limits apply: no static fields, no
    /// structs, no generics/List/LINQ, no properties, no method overloads, no per-frame
    /// allocation (every array is allocated once in <c>Start</c>).
    /// </para>
    /// </remarks>
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class PickupFieldHost : UdonSharpBehaviour
    {
        // Per-prop state. Attached and hidden are both terminal: an absorbed prop is never
        // offered to the queue again.
        private const int StateFree = 0;
        private const int StateAttached = 1;
        private const int StateHidden = 2;

        // Why an absorbed prop was hidden instead of attached. Recorded, not guessed at.
        private const int HiddenReasonNone = 0;
        private const int HiddenReasonPoseRefused = 1;
        private const int HiddenReasonScaleInvalid = 2;
        private const int HiddenReasonNoRoot = 3;
        private const int HiddenReasonNoTransform = 4;

        // The motor's queue is four slots today. This buffer bounds how many nearest props
        // one scan offers it; it is deliberately larger than the queue so a bigger motor
        // capacity needs no change here, and is clamped (with one log) if it is exceeded.
        private const int SelectionCapacity = 8;

        // A mis-wired attachment root (not under the ball) is otherwise invisible: props
        // would attach to a fixed point in the world. One diagnostic line, once.
        private const float AttachmentRootToleranceM = 0.05f;

        // Ceiling on the "could not attach" console lines, so a data error cannot flood.
        private const int HiddenLogLimit = 8;

        private const float DefaultScanIntervalSeconds = 0.05f;
        private const float DefaultActivationRadiusM = 1.6f;

        // The ids this host has in the motor's queue, remembered only to know what to
        // withdraw. Two selections' worth: a scan can hold the nearest few plus the few
        // candidates that were accepted and are waiting for their commit.
        private const int QueuedCapacity = SelectionCapacity * 2;

        // One "scan" console line at most this often, and only when its counts changed.
        private const float ScanLogIntervalSeconds = 2f;

        // How far past the ball's collision radius a blocking or support prop may be and
        // still be registered. It covers one scan interval of travel with slack - 4 m/s *
        // 0.05 s = 0.2 m, so 0.8 m leaves room for a slower scan or a higher speed - and
        // is not a gameplay threshold: a prop closer than this is a wall/step, a prop
        // farther is simply not registered yet (Docs/Design/PickupAbsorb/COLLIDER_PROXY_RUNTIME.md §4.1). The support
        // set reuses this one number on purpose: the design says the two margins are the
        // same, and a second serialized knob could only drift.
        private const float DefaultBlockingMarginM = 0.8f;

        // A prop already in the set stays until it is clearly out of the margin, so a prop
        // hovering at the edge does not add and remove its box every scan. Purely a
        // hysteresis on the selection, not a second "is it blocking" rule.
        private const float DefaultBlockingStickinessM = 0.25f;

        // Same ceiling pattern as HiddenLogLimit: one line per failure category, ever.
        private const int BlockingLogLimit = 3;

        // ---- Spatial index (Docs/Design/PickupAbsorb/PICKUP_FIELD_PERF.md) ----
        private const float DefaultGridCellSizeM = 2f;
        // Ceiling on grid cells; a larger field gets a larger cell instead.
        private const int GridMaxCells = 4096;
        // A static row whose box reaches further than this from its centre would stretch every
        // grid query, so it is scanned in the linear list instead (with its cached position).
        private const float GridMaxHalfDiagonalM = 1.5f;
        // Static rows re-read per frame to catch a mover the propMobile column did not name.
        private const int DriftChecksPerFrame = 8;
        private const float DriftToleranceM = 0.001f;
        private const int DriftLogLimit = 3;

        [Header("Ball")]
        [SerializeField] private KatamariBallMotor motor;
        [SerializeField] private BallPerformanceDiagnostics performanceDiagnostics;
        [SerializeField] private AbsorbFeedbackHost absorbFeedback;
        [SerializeField] private VRCKatamari.BallLayers.CatchNameDisplay catchNameDisplay;
        [Tooltip("This field stores decorative props only and deliberately has no motor.")]
        [SerializeField] private bool decorativeOnly;

        // The scene places this under BallVisualRollRoot: identity local position, scale 1.
        // The host never moves or scales it.
        [SerializeField] private Transform attachmentRoot;

        [Header("Props: one entry per prop, index-aligned")]
        [SerializeField] private Transform[] propTransforms;
        [SerializeField] private string[] itemIds;
        // Baked IDs, not row offsets: editor pickup sync may reorder the rows.
        [SerializeField] private string[] areaCollisionOffItemIds;
        private bool[] areaCollisionOffRows;
        [SerializeField] private string[] kinds;
        // Optional count-goal labels in itemIds order. An empty row is not counted;
        // when the entire column is absent, the original kind tally is used.
        [SerializeField] private string[] objectiveKinds;
        // Optional per-prop scream id (the original's object voice), indexed exactly like
        // itemIds; 0 = no scream. The serialized name predates the meaning and is kept.
        [SerializeField] private int[] propAbsorbSoundClasses;
        // Optional catch-name columns, indexed like itemIds; reference levels only (names are
        // reference material and stay in _RefUnlicensed scenes). Empty or short = no name.
        [SerializeField] private string[] propCatchNamesZh;
        [SerializeField] private string[] propCatchNamesEn;
        [SerializeField] private string[] propCatchNamesJa;
        // propBounds is the prop's WORLD AABB (full edge lengths, metres). The blocking
        // boxes and the support classification read it as an axis-aligned world box, so its
        // meaning must not change. A turned prop's world box is larger than the prop, so it
        // is never the size the absorb gate or the contact reach reads.
        [SerializeField] private Vector3[] propBounds;
        // propLocalBounds is the same prop measured along its OWN local axes (full edge
        // lengths, metres; the prop root's rotation is NOT included): the prop's own size,
        // the original's per-type a, b, c. It feeds the absorb gate, the contact reach and
        // the blocking classification (none depends on how the prop is turned) and the
        // radial-proxy shell, which rotates these sizes by the prop's ball-local rotation
        // (that rotation already contains the root rotation, so passing propBounds would
        // rotate a yawed prop twice). Optional: an un-synced scene has no column and the
        // host falls back to propBounds with one reason-coded warning.
        [SerializeField] private Vector3[] propLocalBounds;
        // Per-item volume factor k (the original's `Volume = k * a*b*c`; 0.70 default).
        // Filled by the level importer from `defs[].volumeK` (a prop's own `volumeK` wins),
        // index-aligned with propTransforms. OPTIONAL: a scene synced before this column
        // existed has an empty/shorter array and every one of its props plays at
        // PickupAbsorbMath.DefaultVolumeK, so no re-sync is needed to stay playable. The
        // value is the GATE's input (Req = floor(1000*cbrt(60*k*a*b*c/pi))). The growth
        // side reads the finished `propGrowthMm3` column below, which the importer prices
        // with the same k (penalty * k * a*b*c), so the two are the same volume per item.
        [SerializeField] private float[] propVolumeK;
        // Growth per prop: the ledger gain one absorb of this row is worth, in mm^3.
        // DERIVED at import/sync (`round(penalty * k * a*b*c * 1e9)`) and never authored
        // (Docs/OPEN_DECISIONS.md (r) C+D; Docs/Research/ContentVolume/FIXED_POINT_STEP_CD.md
        // section 3). `long[]` because the ledger is 64-bit and the value is the gain itself:
        // the runtime does no pricing, no unit conversion and no float hop. A scene synced
        // before this column existed has an empty/short array and the host derives each row
        // once at Start from the prop's own size + propVolumeK + the default 0.8 penalty, the
        // same formula, so an un-reimported lab stays playable.
        [SerializeField] private long[] propGrowthMm3;
        // Optional for props without Unity Colliders. A wired collider is disabled on attach;
        // the motor's analytic pickup and contact proxies do not use this column.
        [SerializeField] private Collider[] propColliders;
        // ---- Optional per-prop collider proxy columns (COLLIDER_PROXY.md §6) ----
        // Index-aligned with propTransforms. kind: -1 = no block (the prop falls back to
        // its OBB, exactly today's collision shape), 0 box, 1 sphere, 2 capsule,
        // 3 convex. All shape numbers are in the prop's LOCAL space; the host bakes them
        // with the prop's world pose once per scan. An un-synced scene has null/short
        // columns and every row reads -1, so the flag-on path still keeps the old boxes.
        [SerializeField] private int[] propProxyKinds;
        [SerializeField] private Vector3[] propProxyCenters;
        [SerializeField] private Vector3[] propProxySizes;
        [SerializeField] private float[] propProxyRadii;
        [SerializeField] private float[] propProxyHalfHeights;
        [SerializeField] private int[] propProxyAxes;
        [SerializeField] private int[] propProxyHullStarts;
        [SerializeField] private int[] propProxyHullCounts;
        [SerializeField] private Vector3[] propProxyHullPoints;
        [SerializeField] private int[] propProxyFaceStarts;
        [SerializeField] private int[] propProxyFaceCounts;
        [SerializeField] private int[] propProxyHullFaces;

        [Header("Stack fall (optional, PROP_BEHAVIOURS 10.9)")]
        // Single parent pointer per row: the HOST ROW index of the prop this one stands on
        // or belongs to, -1 = none. Written by PickupFieldSync from the level's `parent`
        // (idempotent, by item id); an absent or short array reads as -1. Only the stack-fall
        // host reads it, through GetPropParentIndex.
        [SerializeField] private int[] propParentIndex;
        // Optional. When wired, this host tells it about every absorbed row and asks it to
        // restore moved props on a field reset; it is also asked whether a row is falling so
        // a falling prop is neither a wall nor a floor. Null = no stack fall (old behaviour).
        [SerializeField] private PropFallHost fallHost;
        // The level's arena host. A prop that has a same-id static solid (the reference level's
        // big scenery) takes that solid out of the motor when it is rolled up, and a field reset
        // puts it back. Null = nothing is removed (older scenes, levels with no twin solids);
        // the importer wires it, and the host's staticBoxItemIds says which boxes are twins.
        [SerializeField] private LevelArenaHost arenaHost;

        [Header("Attached display (AttachedDisplayMath)")]
        // How many attached props the ball draws: the biggest N plus the most recent N not
        // already among them (owner 2026-11-23: 10 + 10 for the local ball). 0 in an older
        // scene means the default. Props buried inside the ball are never drawn.
        [SerializeField] private int displayBiggestCap = AttachedDisplayMath.DefaultBiggestCount;
        [SerializeField] private int displayRecentCap = AttachedDisplayMath.DefaultRecentCount;

        [Header("Radii and cadence")]
        [SerializeField] private float activationRadiusM = DefaultActivationRadiusM;
        [SerializeField] private float scanIntervalSeconds = DefaultScanIntervalSeconds;

        [Header("Prop obstacles (absorb-proof props block or support the ball)")]
        // Named as the DISABLED flag on purpose: a field that is absent from an already
        // serialized scene comes in at the CLR default (false), and "false" must mean
        // "the props are solid". With `blockingEnabled = true` a stale serialization could
        // silently switch the whole feature off, and the symptom would be exactly the bug
        // this file exists to fix. Therefore this flag defaults to false = enabled.
        // P2: this one switch governs BOTH roles - a disabled field registers neither a
        // wall nor a support, so the ball's query is the pre-P2 empty table. Two flags
        // would let a scene claim "props are steps but not walls", which is not a state
        // the classifier produces.
        [SerializeField] private bool blockingDisabled = false;
        [SerializeField] private float blockingMarginM = DefaultBlockingMarginM;
        [SerializeField] private float blockingStickinessM = DefaultBlockingStickinessM;

        [Header("Spatial index (static props by grid cell, moving props scanned)")]
        // Per row: true when something else (the motion host, a rider on a moving parent) may
        // move this prop's Transform, so every scan must read it live. false rows are looked up
        // by grid cell from the position read once in Start. OPTIONAL: an absent or short
        // column makes EVERY row a moving row (the scan reads each Transform, as it did before
        // the index existed), so an un-synced scene stays exactly correct, only not faster.
        // Written by PickupFieldSync from the level's motion rows. The stack fall marks rows it
        // moves at run time (MarkPropMobile); a drift check catches the rest, with a warning.
        [SerializeField] private bool[] propMobile;
        // Grid cell edge, metres. 0 / absent in an older scene means the default (2 m).
        [SerializeField] private float gridCellSizeM = DefaultGridCellSizeM;

        // ---- Per-prop store (allocated in Start, never serialized) ----
        private int[] propStates;
        // The rows this host has queued in the motor (see QueuedCapacity): the withdraw list.
        private int[] queuedIndices;
        // Last pose actually sent to the motor. Its idempotent submit deliberately does not
        // replace a pending snapshot, so selected movers need the pose-only refresh API.
        private Vector3[] submittedPositions;
        private Quaternion[] submittedRotations;
        private int queuedCount;
        // Where each prop started, recorded once in Start before anything can attach:
        // parent, local pose and active flag. ResetField is the only reader.
        private Transform[] originalParents;
        private Vector3[] originalLocalPositions;
        private Quaternion[] originalLocalRotations;
        private bool[] originalActiveStates;
        // Rows the motor could only refuse forever. Queued once, such a row would sit in one
        // of the four slots and be re-refused on every step (the motor never drops a refused
        // candidate), starving the props the ball is actually touching. Fixed at start-up so
        // the scan costs one array read per prop.
        private bool[] submittable;
        // The growth actually offered to the motor: the serialized column when it is valid,
        // otherwise the Start-time fallback (own size x k x 0.8). Filled once in Start.
        private long[] growthMm3;
        // The prop's own three edge lengths, resolved once in Start: propLocalBounds when
        // the row is usable, else the world box. The gate, the blocking predicate, the
        // growth fallback and the radial proxy all read this one array.
        private Vector3[] propOwnSizes;
        // EffectiveVolumeK per row, resolved once in Start, so the scan's size test reads an
        // array instead of re-resolving k for every prop.
        private float[] propGateK;
        private int growthFallbackRows;

        // Bounded nearest-first selection for one scan.
        private int[] nearestIndices;
        private float[] nearestDistSq;
        // What the last scan saw, for the diagnostics getters and the throttled scan line:
        // props inside the activation radius, those of them that pass the size test, and the
        // rows chosen for the queue.
        private int lastScanInRange;
        private int lastScanWithinLimit;
        private int lastScanSelected;
        private int lastScanQueued;
        private int loggedScanInRange;
        private int loggedScanWithinLimit;
        private int loggedScanQueued;
        private float nextScanLogTime;

        // ---- Prop-obstacle store (allocated in Start, never serialized) ----
        // One row per prop, indexed like every other parallel array. propUsable and
        // propHalfDiagonal are fixed in Start (both depend only on propBounds, which
        // does not change); the boxes are rebuilt per scan from the prop's current world
        // position so a prop another system moved cannot leave a stale wall behind. The
        // SAME row feeds both roles: one classification gives a prop one role per scan,
        // so the blocking and support selections read one box, never two.
        private bool[] propUsable;
        private float[] propHalfDiagonal;
        private Vector3[] propMinima;
        private Vector3[] propMaxima;
        // Per-scan candidate flags. They are set only for the rows in candRows and cleared from
        // that list at the start of the next scan, so a stale true cannot survive. The
        // classifier gives a row at most one role: supportCandidate marks the support rows,
        // propObbCandidate every candidate (blocking or support).
        private bool[] supportCandidate;
        private int[] candRows;
        private int candCount;
        private float[] candDistanceByRow;
        private int propUsableCount;
        private float nextBlockingScanTime;
        // Applied prop solids by role, as of the last scan (blocking = role 3, support = 2).
        private int propObbBlockingCount;
        private int propObbSupportCount;

        // ---- Spatial index (allocated in Start, never serialized) ----
        // propWorldPositions: world position read once in Start; valid for a row that is not
        // mobile. propReqMm: the absorb requirement per row (or invalid), so the size test is
        // an integer compare instead of a cube root per row per scan.
        private Vector3[] propWorldPositions;
        private float[] propReqMm;
        private bool[] rowMobile;
        // tracked: the row can ever matter (a transform and a scan or obstacle role).
        private bool[] rowTracked;
        private bool[] rowInLinear;
        private bool[] propBoxValid;
        // Rows scanned every time: mobile rows and static rows with a big box.
        private int[] linearRows;
        private int linearCount;
        // Every tracked static row, for the drift check.
        private int[] staticRows;
        private int staticCount;
        private int driftCursor;
        private int driftLogCount;
        private int mobileRowCount;
        private int bigStaticRowCount;
        private bool gridActive;
        private float[] gridInfo;
        private int[] gridCellStart;
        private int[] gridCellRows;
        private Vector3[] gridCellMin;
        private Vector3[] gridCellMax;
        private int gridMemberCount;
        private float gridMaxHalfDiagonal;
        // The free rows near the ball for the stages of this Update: gathered once, read by
        // the obstacle stage and the candidate scan. Valid only inside Update.
        private int[] queryRows;
        private int[] nearRows;
        private float[] nearDistSq;
        private Vector3[] nearPositions;
        private int nearCount;
        private bool nearValid;
        private float nearScanReachM;
        private float nearPruneReachM;

        // ---- Contact prop solids (CONTACT_MODEL_V2 §4A.4/§5.2, stage S4b) ----
        // One TRUE OBB per selected prop for the v2 contact solver. Built from
        // propLocalBounds + the prop's world rotation, never from the double-rotated world
        // AABB (RadialProxyLocalBoundsTests pins that bug). The set is refreshed on the same
        // scan from the candidate flags above, so a prop is a wall or a floor and never two
        // things at once. It has its own stickiness memory and a 12-slot capacity (6 + 6).
        private bool[] propObbCandidate;
        private int[] propObbPreviousIndices;
        private int[] propObbSelectedIndices;
        private float[] propObbSelectedDistances;
        private Vector3[] propObbCenter;
        private Vector3[] propObbAxisX;
        private Vector3[] propObbAxisY;
        private Vector3[] propObbAxisZ;
        private Vector3[] propObbHalfExtents;
        private int[] propObbSurfaceId;
        private int[] propObbRole;
        private int propObbCount;
        private int propObbPreviousCount;
        private int propObbCandidateCount;
        private int propObbFallbackRows;
        private int propObbOverflowCount;
        // What the motor's prop block currently holds (its SetPropObbs is a whole-block
        // replace; 0 means "cleared"), and the refusal-log counter.
        private int propObbAppliedCount;
        private int propObbLogCount;

        // ---- Contact v2 collider proxies (COLLIDER_PROXY.md §6) ----
        private Vector3[] proxyCenter;
        private Vector3[] proxyAxisX;
        private Vector3[] proxyAxisY;
        private Vector3[] proxyAxisZ;
        private Vector3[] proxyHalfExtents;
        private float[] proxyRadii;
        private float[] proxyHalfHeights;
        private int[] proxyAxes;
        private int[] proxyKinds;
        private int[] proxySurfaceIds;
        private int[] proxyRoles;
        private Vector3[] proxyHullPoints;
        private int[] proxyHullStarts;
        private int[] proxyHullCounts;
        private Vector3[] proxyFaceNormals;
        private float[] proxyFaceOffsets;
        private int[] proxyFaceA;
        private int[] proxyFaceB;
        private int[] proxyFaceC;
        private int[] proxyFaceStarts;
        private int[] proxyFaceCounts;
        private int proxyCount;
        private int proxyCandidateCount;
        private int proxyFallbackRows;
        private int proxyAppliedCount;
        private int proxyLogCount;

        // ---- Attached display (allocated in Start, never serialized) ----
        // Per row: attach order (-1 = not attached), volume key, buried flag, farthest box
        // corner from the ball centre in whole mm. displayHist lists attached rows in attach
        // order; displaySorted holds the live rows by farthest corner, buried up to the cursor.
        private int[] displaySeq;
        private long[] displayKey;
        private bool[] displayBuried;
        private int[] displayFarMm;
        private int[] displayHist;
        private int displayHistCount;
        private int[] displaySorted;
        private int displaySortedCount;
        private int displayBuryCursor;
        private int[] displayBig;
        private int displayBigCount;
        private int[] displayRecent;
        private int displayRecentCount;
        private int[] displayBigBefore;
        private int[] displayRecentBefore;
        private Vector3[] displayCorners;
        // Every renderer under an attached prop, recorded the frame it attaches, with its
        // enabled flag at that moment (the flag ResetField puts back).
        private Renderer[] displayRenderers;
        private bool[] displayRendererWasEnabled;
        private int displayRendererCount;
        private int[] displayRendererStart;
        private int[] displayRendererLength;
        private int displayShownCount;
        private int buriedCount;
        private int displayCheckedRadiusMm;
        private int displayCheckedAttached;

        // Attached count by kind, grown in place; capacity is the prop count.
        private string[] kindTallyNames;
        private int[] kindTallyCounts;
        private int kindTallyCount;

        private int propCount;
        private int submittableCount;
        private int attachedCount;
        private int hiddenCount;
        private int lastHiddenReason;
        private string lastHiddenItemId;
        private int hiddenLogCount;

        // Absolute cursor into the motor's absorbed-event ring.
        private int seenEvents;

        private bool collectingEnabled;
        private bool collectingInitialised;
        private float nextScanTime;
        private bool loggedLostEvents;
        private bool loggedMotorMissing;
        private bool loggedRootMissing;
        private bool loggedRootMismatch;
        private bool loggedSelectionCapacity;
        private bool loggedNoColliders;
        // Rows whose own size had to come from the world-AABB column. Fixed once in Start,
        // reported in the ready line and in one reason-coded warning.
        private int ownSizeFallbackRows;

        private void Start()
        {
            ClampSettings();
            ResolvePropCount();
            AllocatePropStore();
            RecordOriginalPoses();
            ResolveOwnSizes();
            ResolveGateK();
            ResolveGrowthMm3();
            MarkSubmittable();
            PreparePropRows();
            ResolveAreaCollisionRows();
            BuildSpatialIndex();
            ReportPropDataProblems();
            seenEvents = 0;
            nextScanTime = 0f;
            nextBlockingScanTime = 0f;
            Debug.Log("[PickupFieldHost] ready props=" + propCount
                + " submittable=" + submittableCount
                + " propRows=" + propUsableCount
                + " index=" + (gridActive ? "grid" : "linear")
                + " gridRows=" + gridMemberCount
                + " linearRows=" + linearCount
                + " mobileRows=" + mobileRowCount
                + " mobileColumn=" + (propMobile != null && propMobile.Length >= propCount ? "yes" : "absent")
                + " propObstacles=" + (blockingDisabled ? "off" : "on")
                + " ownSize=" + (ownSizeFallbackRows > 0 ? "fallback" : "local")
                + " activationR=" + activationRadiusM
                + " scanInterval=" + scanIntervalSeconds);
        }

        /// <summary>
        /// Reads the absorbed-event ring, refreshes the prop-obstacle sets, and while
        /// collecting offers nearby props.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The ring is consumed on every frame even when collecting is off: those events
        /// belong to props that were absorbed legitimately before the stop, and dropping
        /// them would leave exactly the "prop vanished" symptom S3 exists to remove. Unity
        /// does not promise that this Update runs after the motor's, so an event may be
        /// picked up one frame late; the ring is monotonic, so that is a delay, not a loss.
        /// </para>
        /// <para>
        /// The prop-obstacle sets are refreshed BEFORE the collecting gate and before the
        /// attachment-root check, because both of those only govern absorption: a round
        /// that stopped collecting, or a field whose props can only be hidden, is still a
        /// solid world. Skipping the refresh there is what would let the ball roll through
        /// everything.
        /// </para>
        /// </remarks>
        public void Update()
        {
            bool profile = performanceDiagnostics != null && performanceDiagnostics.sampling;
            long frameStart = profile ? BallPerformanceMath.Timestamp() : 0L;
            UpdateFrame();
            // The gathered rows are only valid for the stages of one Update.
            nearValid = false;
            if (profile) BallPerformanceMath.Split(performanceDiagnostics, BallPerformanceMath.PickupFrame, frameStart);
        }

        private void UpdateFrame()
        {
            if (motor == null)
            {
                if (!decorativeOnly) LogMotorMissingOnce();
                return;
            }
            if (propCount <= 0) return;

            // A positive semantic diameter is this motor's colour of readiness: it is what its
            // Start derives, and LevelArenaHost gates its registration on the same test.
            // Until then the motor's tables describe a zero-diameter ball at the world origin
            // - its gate diameter is 0 mm ("every prop is too big"), nothing can be a support
            // - so a set built here would describe a ball that does not exist. Waiting is the
            // host's half of the readiness contract; that alone is NOT what fixes F1 (there
            // the motor had derived a size in its own step while its Start had never run, so
            // the set still had to arrive): the motor's entry points allocate on first use,
            // so the two halves cannot disagree.
            if (motor.GetSemanticDiameterWorldM() <= 0f) return;

            bool profile = performanceDiagnostics != null && performanceDiagnostics.sampling;
            long phaseStart = profile ? BallPerformanceMath.Timestamp() : 0L;
            ConsumeAbsorbedEvents();
            UpdateBuried();
            if (profile) phaseStart = BallPerformanceMath.Split(performanceDiagnostics, BallPerformanceMath.AttachmentEvents, phaseStart);
            VerifyStaticRows();
            UpdatePropObstacles();
            if (profile)
            {
                BallPerformanceMath.Split(performanceDiagnostics, BallPerformanceMath.PickupObstacles, phaseStart);
                performanceDiagnostics._Sample(BallPerformanceMath.Attached, attachedCount);
            }
            if (!Collecting()) return;
            if (attachmentRoot == null)
            {
                // Submitting without a root would absorb props the host can only hide, so it
                // stops here and says so once instead of eating the field silently.
                LogRootMissingOnce();
                return;
            }
            ScanProps();
        }

        /// <summary>
        /// Enables or disables candidate submission for this field.
        /// </summary>
        /// <remarks>
        /// <para>
        /// A round controller calls this with false at time-out: submitting stops and every
        /// candidate this host queued is released, so a frozen round cannot absorb anything
        /// more. Only ids this host submitted are withdrawn - <c>ClearCandidates</c> would
        /// also drop another host's work on the same motor. Props already attached are left
        /// attached: they were absorbed, and un-parenting them would be the disappearing
        /// prop again.
        /// </para>
        /// <para>
        /// Prop obstacles are deliberately NOT affected: a stopped round is still a solid
        /// world, so this only withdraws candidates. The prop that stopped the ball keeps
        /// stopping it and the prop the ball stands on keeps supporting it, which is what
        /// makes a time-out feel like a frozen game rather than a ghost level.
        /// </para>
        /// </remarks>
        public void SetCollecting(bool collecting)
        {
            collectingEnabled = collecting;
            collectingInitialised = true;
            if (collecting) return;
            if (absorbFeedback != null) absorbFeedback.ClearFeedback();
            WithdrawSubmitted();
        }

        /// <summary>
        /// Puts the field back the way it was at Start: every prop this host attached or
        /// hid returns to its recorded parent, local pose and active flag, becomes free
        /// again and can be absorbed in the new round.
        /// </summary>
        /// <returns>
        /// True when the field is reset (an empty field counts as reset); false only
        /// before <c>Start</c> has allocated the rows, in which case nothing was touched.
        /// </returns>
        /// <remarks>
        /// <para>
        /// Called by the story round's retry, after the motor has accepted
        /// <c>TryResetForNewRound</c> and before the countdown starts again. The
        /// absorbed-event cursor (<c>seenEvents</c>) is deliberately NOT rewound: the
        /// motor's absolute absorbed count is monotonic across resets
        /// (Docs/Design/BallMotor/README.md, "吸收事件环的绝对计数保持单调"), so keeping
        /// the cursor means the new round's events continue at the same absolute numbers
        /// and no dead event is replayed.
        /// </para>
        /// <para>
        /// What is recorded is parent, local position, local rotation and active flag, in
        /// <c>Start</c>. Local scale is not recorded because attach never writes it:
        /// <c>SetParent</c> preserves world scale by adjusting local scale, so returning
        /// to the original parent restores the authored look. Wired colliders are
        /// re-enabled unconditionally (attach and hide only ever disable them), so a
        /// collider authored disabled would come back enabled; that is the trade for not
        /// keeping a second per-prop flag.
        /// </para>
        /// <para>
        /// Only attached or hidden props are moved. A prop that stayed free was never
        /// touched by this host, so a move another system made to it is not undone here.
        /// Session diagnostic latches (<c>loggedLostEvents</c>, <c>hiddenLogCount</c>)
        /// are kept, for the same reason the motor keeps its log caps: unlimited retries
        /// must not refill the console. The collecting gate is not touched either; the
        /// round controller reopens it in <c>BeginRound</c>.
        /// </para>
        /// </remarks>
        public bool ResetField()
        {
            if (absorbFeedback != null) absorbFeedback.ClearFeedback();
            if (propStates == null || propTransforms == null) return false;

            // Withdraw first: the attached/hidden rows no longer hold a submit time, so
            // this only covers rows that were queued and left free.
            WithdrawSubmitted();
            RestoreDisplayRenderers();

            int restored = 0;
            for (int i = 0; i < propCount; i++)
            {
                if (propStates[i] == StateFree) continue;
                Transform prop = propTransforms[i];
                if (prop != null)
                {
                    // Same order as attach: parent, then write the local pose.
                    prop.SetParent(originalParents[i]);
                    prop.localPosition = originalLocalPositions[i];
                    prop.localRotation = originalLocalRotations[i];
                    prop.gameObject.SetActive(originalActiveStates[i]);
                }
                if (propColliders != null && i < propColliders.Length && propColliders[i] != null)
                    propColliders[i].enabled = true;
                propStates[i] = StateFree;
                restored = restored + 1;
            }
            // Props the stack fall moved while they were free go back to where they started
            // (attached ones were restored by the loop above).
            if (fallHost != null) fallHost.ResetFalls();
            // The solids that left with their props come back with them. A no-op when the
            // motor's static table was dropped meanwhile (a level switch): the next Register
            // bakes every box again, so nothing is registered twice.
            if (arenaHost != null) arenaHost.RestoreRemovedSolids();

            // The withdraw list goes too: the caller has already had the motor clear its
            // candidate queue, so nothing this host listed is queued any more and the next
            // scan starts from an empty list.
            queuedCount = 0;

            // The prop solids are a function of where the ball is, so nothing about them
            // survives a reset; static planes and boxes stay in the motor (the map did not
            // move). Clearing here as well as in TeleportTo covers a reset that does not
            // teleport, and the next scan rebuilds from the restored field.
            // Every registered proxy item is one of the props just restored, so the shell
            // is emptied rather than edited. Clearing here as well as in the motor's own
            // round reset covers a field reset that does not restart the ball.
            if (motor != null) motor.ClearRadialProxies();
            ClearPropObbs();
            ClearPropProxies();
            nextBlockingScanTime = 0f;

            attachedCount = 0;
            hiddenCount = 0;
            lastHiddenReason = HiddenReasonNone;
            lastHiddenItemId = "";
            // Tallies are counted by walking kindTallyNames up to kindTallyCount, so a
            // zero count hides every previous entry without clearing the array.
            kindTallyCount = 0;
            // The next Update scans at once instead of waiting out the old cadence.
            nextScanTime = 0f;
            Debug.Log("[PickupFieldHost] field reset restored=" + restored
                + " props=" + propCount);
            return true;
        }

        /// <summary>The prop's Transform, or null for a bad index (read-only accessor).</summary>
        public Transform GetPropTransform(int index)
        {
            if (propTransforms == null || index < 0 || index >= propCount) return null;
            return propTransforms[index];
        }

        /// <summary>The prop's world AABB full edge lengths (propBounds), or zero.</summary>
        public Vector3 GetPropBounds(int index)
        {
            if (propBounds == null || index < 0 || index >= propCount) return Vector3.zero;
            return propBounds[index];
        }

        /// <summary>
        /// Host row index of the prop's parent, -1 when it has none or the column is absent
        /// or short. Not validated here: the stack-fall host sanitizes the whole column once.
        /// </summary>
        public int GetPropParentIndex(int index)
        {
            if (propParentIndex == null || index < 0 || index >= propParentIndex.Length) return -1;
            return propParentIndex[index];
        }

        /// <summary>True while the row is neither attached nor hidden (still in the world).</summary>
        public bool IsPropFree(int index)
        {
            if (propStates == null || index < 0 || index >= propCount) return false;
            return propStates[index] == StateFree;
        }

        /// <summary>Total props attached to the ball by this host.</summary>
        public int GetAttachedCount()
        {
            return attachedCount;
        }

        /// <summary>
        /// Attached props of one kind, for a category goal. Ordinal text match, same as the
        /// motor's allowed-kind set; an unknown kind returns 0.
        /// </summary>
        public int GetAttachedCountForKind(string kind)
        {
            if (kind == null) return 0;
            for (int i = 0; i < kindTallyCount; i++)
            {
                if (kindTallyNames[i] == kind) return kindTallyCounts[i];
            }
            return 0;
        }

        /// <summary>Attached props whose renderers are drawn (the display cap's B and R groups).</summary>
        public int GetDisplayedCount()
        {
            return displayShownCount;
        }

        /// <summary>Attached props buried inside the ball: never drawn again this round.</summary>
        public int GetBuriedCount()
        {
            return buriedCount;
        }

        /// <summary>Absorbed props this host could not attach and therefore hid.</summary>
        public int GetHiddenCount()
        {
            return hiddenCount;
        }

        /// <summary>Reason code of the last hidden prop; 0 before any hide.</summary>
        public int GetLastHiddenReason()
        {
            return lastHiddenReason;
        }

        public string GetLastHiddenItemId()
        {
            return lastHiddenItemId;
        }

        public int GetPropCount()
        {
            return propCount;
        }

        /// <summary>Declared proxies that had to fall back to the OBB on the last scan.</summary>
        public int GetPropProxyFallbackRows()
        {
            return proxyFallbackRows;
        }

        /// <summary>
        /// Rows whose own size (absorb gate, blocking predicate, radial proxy) used the
        /// world AABB instead of <c>propLocalBounds</c>; 0 means every row used the local
        /// column.
        /// </summary>
        public int GetOwnSizeFallbackRows()
        {
            return ownSizeFallbackRows;
        }

        /// <summary>One row's resolved own size (zero when the row is outside the field).</summary>
        public Vector3 GetPropOwnSize(int index)
        {
            if (propOwnSizes == null || index < 0 || index >= propOwnSizes.Length)
                return Vector3.zero;
            return propOwnSizes[index];
        }

        /// <summary>The prop's scream id, or 0 when the column is missing or misaligned.</summary>
        public int GetPropAbsorbSoundClass(int index)
        {
            if (propAbsorbSoundClasses == null || itemIds == null
                || propAbsorbSoundClasses.Length != itemIds.Length || index < 0
                || index >= propAbsorbSoundClasses.Length) return 0;
            return propAbsorbSoundClasses[index];
        }

        private string CatchName(string[] column, int index)
        {
            if (column == null || index < 0 || index >= column.Length || column[index] == null) return "";
            return column[index];
        }

        public bool IsCollecting()
        {
            return Collecting();
        }

        private bool Collecting()
        {
            // Collecting is on until a round controller says otherwise; the field is not
            // serialized so a fresh Play Mode run always starts from the same place.
            if (!collectingInitialised) return true;
            return collectingEnabled;
        }

        private void ClampSettings()
        {
            if (!KatamariMath.IsFiniteFloat(activationRadiusM) || activationRadiusM <= 0f)
                activationRadiusM = DefaultActivationRadiusM;
            if (!KatamariMath.IsFiniteFloat(scanIntervalSeconds) || scanIntervalSeconds <= 0f)
                scanIntervalSeconds = DefaultScanIntervalSeconds;
            if (!KatamariMath.IsFiniteFloat(blockingMarginM) || blockingMarginM < 0f)
                blockingMarginM = DefaultBlockingMarginM;
            if (!KatamariMath.IsFiniteFloat(blockingStickinessM) || blockingStickinessM < 0f)
                blockingStickinessM = DefaultBlockingStickinessM;
            if (!KatamariMath.IsFiniteFloat(gridCellSizeM) || gridCellSizeM <= 0f)
                gridCellSizeM = DefaultGridCellSizeM;
            if (displayBiggestCap <= 0) displayBiggestCap = AttachedDisplayMath.DefaultBiggestCount;
            if (displayRecentCap <= 0) displayRecentCap = AttachedDisplayMath.DefaultRecentCount;
        }

        /// <summary>
        /// Fixes the usable prop count at the shortest parallel array, so one short row can
        /// never index past another array.
        /// </summary>
        private void ResolvePropCount()
        {
            propCount = 0;
            if (propTransforms == null) return;
            propCount = propTransforms.Length;
            propCount = Smaller(propCount, LengthOfIds());
            propCount = Smaller(propCount, LengthOfKinds());
            propCount = Smaller(propCount, LengthOfBounds());
            // The growth column is OPTIONAL (a pre-C+D scene has none), so it does not cap
            // propCount: a row without a serialized value gets the Start-time fallback.
            if (propGrowthMm3 != null && propGrowthMm3.Length < propCount)
            {
                growthFallbackRows = propCount - propGrowthMm3.Length;
            }
        }

        private void ResolveAreaCollisionRows()
        {
            if (areaCollisionOffItemIds == null || areaCollisionOffItemIds.Length == 0) return;
            areaCollisionOffRows = new bool[propCount];
            int resolved = 0;
            int unresolved = 0;
            for (int i = 0; i < areaCollisionOffItemIds.Length; i++)
            {
                string id = areaCollisionOffItemIds[i];
                bool found = false;
                for (int row = 0; row < propCount; row++)
                {
                    if (string.IsNullOrEmpty(id) || itemIds[row] != id) continue;
                    found = true;
                    if (!areaCollisionOffRows[row]) resolved++;
                    areaCollisionOffRows[row] = true;
                }
                if (!found) unresolved++;
            }
            string message = "[pickup] area collision off: " + areaCollisionOffItemIds.Length +
                " ids, " + resolved + " rows resolved, " + unresolved + " unresolved";
            if (unresolved > 0) Debug.LogError(message);
            else Debug.Log(message);
        }

        private int LengthOfIds()
        {
            if (itemIds == null) return 0;
            return itemIds.Length;
        }

        private int LengthOfKinds()
        {
            if (kinds == null) return 0;
            return kinds.Length;
        }

        private int LengthOfBounds()
        {
            if (propBounds == null) return 0;
            return propBounds.Length;
        }

        /// <summary>
        /// The per-item volume factor k the gate must use for one row.
        /// </summary>
        /// <remarks>
        /// The importer/sync writes <c>propVolumeK</c>; a scene without the column, a short
        /// array or a row the authoring rule refuses falls back to
        /// <see cref="PickupAbsorbMath.DefaultVolumeK"/>. The fallback is silent at run time
        /// on purpose: a missing column is the documented pre-field state, and the reason a
        /// scene can be re-synced later without becoming unplayable in the meantime. The
        /// authoring side is where a half-filled column is made visible - the sync refuses
        /// an out-of-range k and reports how many rows got the default.
        /// </remarks>
        private float EffectiveVolumeK(int index)
        {
            if (propVolumeK == null || index < 0 || index >= propVolumeK.Length)
                return PickupAbsorbMath.DefaultVolumeK;
            float value = propVolumeK[index];
            if (!KatamariMath.IsFiniteFloat(value)) return PickupAbsorbMath.DefaultVolumeK;
            if (value <= 0f || value > PickupAbsorbMath.MaxVolumeK)
                return PickupAbsorbMath.DefaultVolumeK;
            return value;
        }

        // Fills propGateK once: EffectiveVolumeK is the only resolver of a row's k.
        private void ResolveGateK()
        {
            for (int i = 0; i < propCount; i++) propGateK[i] = EffectiveVolumeK(i);
        }

        // Two tiny comparators rather than Mathf calls, because the prop count is fixed by
        // the shortest parallel array and this code must not depend on a Mathf overload that
        // UdonSharp may not bind.
        private int Smaller(int a, int b)
        {
            return a < b ? a : b;
        }

        private int Larger(int a, int b)
        {
            return a > b ? a : b;
        }

        private void AllocatePropStore()
        {
            propStates = new int[propCount];
            queuedIndices = new int[QueuedCapacity];
            submittedPositions = new Vector3[propCount];
            submittedRotations = new Quaternion[propCount];
            queuedCount = 0;
            propGateK = new float[propCount];
            lastScanInRange = 0;
            lastScanWithinLimit = 0;
            lastScanSelected = 0;
            lastScanQueued = 0;
            loggedScanInRange = -1;
            loggedScanWithinLimit = -1;
            loggedScanQueued = -1;
            nextScanLogTime = 0f;
            originalParents = new Transform[propCount];
            originalLocalPositions = new Vector3[propCount];
            originalLocalRotations = new Quaternion[propCount];
            originalActiveStates = new bool[propCount];
            submittable = new bool[propCount];
            growthMm3 = new long[propCount];
            propOwnSizes = new Vector3[propCount];
            growthFallbackRows = 0;
            nearestIndices = new int[SelectionCapacity];
            nearestDistSq = new float[SelectionCapacity];
            propUsable = new bool[propCount];
            propHalfDiagonal = new float[propCount];
            propMinima = new Vector3[propCount];
            propMaxima = new Vector3[propCount];
            propUsableCount = 0;
            supportCandidate = new bool[propCount];
            propObbCandidate = new bool[propCount];
            propObbPreviousIndices = new int[PropObbBakeMath.PropObbCapacity];
            propObbSelectedIndices = new int[PropObbBakeMath.PropObbCapacity];
            propObbSelectedDistances = new float[PropObbBakeMath.PropObbCapacity];
            propObbCenter = new Vector3[PropObbBakeMath.PropObbCapacity];
            propObbAxisX = new Vector3[PropObbBakeMath.PropObbCapacity];
            propObbAxisY = new Vector3[PropObbBakeMath.PropObbCapacity];
            propObbAxisZ = new Vector3[PropObbBakeMath.PropObbCapacity];
            propObbHalfExtents = new Vector3[PropObbBakeMath.PropObbCapacity];
            propObbSurfaceId = new int[PropObbBakeMath.PropObbCapacity];
            propObbRole = new int[PropObbBakeMath.PropObbCapacity];
            propObbCount = 0;
            propObbPreviousCount = 0;
            propObbCandidateCount = 0;
            propObbFallbackRows = 0;
            propObbOverflowCount = 0;
            propObbAppliedCount = 0;
            propObbLogCount = 0;

            int proxyCapacity = PropObbBakeMath.PropObbCapacity;
            proxyCenter = new Vector3[proxyCapacity];
            proxyAxisX = new Vector3[proxyCapacity];
            proxyAxisY = new Vector3[proxyCapacity];
            proxyAxisZ = new Vector3[proxyCapacity];
            proxyHalfExtents = new Vector3[proxyCapacity];
            proxyRadii = new float[proxyCapacity];
            proxyHalfHeights = new float[proxyCapacity];
            proxyAxes = new int[proxyCapacity];
            proxyKinds = new int[proxyCapacity];
            proxySurfaceIds = new int[proxyCapacity];
            proxyRoles = new int[proxyCapacity];
            proxyHullPoints = new Vector3[proxyCapacity * ProxyContactMath.MaxHullPoints];
            proxyHullStarts = new int[proxyCapacity];
            proxyHullCounts = new int[proxyCapacity];
            proxyFaceNormals = new Vector3[proxyCapacity * ProxyContactMath.MaxHullFaces];
            proxyFaceOffsets = new float[proxyCapacity * ProxyContactMath.MaxHullFaces];
            proxyFaceA = new int[proxyCapacity * ProxyContactMath.MaxHullFaces];
            proxyFaceB = new int[proxyCapacity * ProxyContactMath.MaxHullFaces];
            proxyFaceC = new int[proxyCapacity * ProxyContactMath.MaxHullFaces];
            proxyFaceStarts = new int[proxyCapacity];
            proxyFaceCounts = new int[proxyCapacity];
            proxyCount = 0;
            proxyCandidateCount = 0;
            proxyFallbackRows = 0;
            proxyAppliedCount = 0;
            proxyLogCount = 0;
            kindTallyNames = new string[propCount];
            kindTallyCounts = new int[propCount];
            kindTallyCount = 0;
            displaySeq = new int[propCount];
            displayKey = new long[propCount];
            displayBuried = new bool[propCount];
            displayFarMm = new int[propCount];
            displayHist = new int[propCount];
            displaySorted = new int[propCount];
            displayBig = new int[displayBiggestCap];
            displayRecent = new int[displayRecentCap];
            displayBigBefore = new int[displayBiggestCap];
            displayRecentBefore = new int[displayRecentCap];
            displayCorners = new Vector3[RadialProxyShellMath.AabbCornerCount];
            displayRenderers = new Renderer[propCount > 0 ? propCount : 1];
            displayRendererWasEnabled = new bool[displayRenderers.Length];
            displayRendererStart = new int[propCount];
            displayRendererLength = new int[propCount];
            ClearDisplayState();
            attachedCount = 0;
            hiddenCount = 0;
            lastHiddenReason = HiddenReasonNone;
            lastHiddenItemId = "";
        }

        /// <summary>
        /// Records where each prop starts - parent, local pose, active flag - once, in
        /// Start, before any prop can be attached. ResetField is the only reader.
        /// </summary>
        /// <remarks>
        /// A null parent is recorded as null and passed back to <c>SetParent</c>, which
        /// Unity treats as "make it a scene root": props authored at the root come back
        /// to the root rather than under whatever object happened to own them before.
        /// </remarks>
        private void RecordOriginalPoses()
        {
            for (int i = 0; i < propCount; i++)
            {
                Transform prop = propTransforms[i];
                if (prop == null) continue;
                originalParents[i] = prop.parent;
                originalLocalPositions[i] = prop.localPosition;
                originalLocalRotations[i] = prop.localRotation;
                originalActiveStates[i] = prop.gameObject.activeSelf;
            }
        }

        /// <summary>
        /// Marks the rows the motor could queue, so the scan never offers it a row that can
        /// only be refused. Every rejected row named here is explained by the start-up report.
        /// </summary>
        private void MarkSubmittable()
        {
            submittableCount = 0;
            for (int i = 0; i < propCount; i++)
            {
                submittable[i] = IsSubmittable(i);
                if (submittable[i]) submittableCount = submittableCount + 1;
            }
        }

        /// <summary>
        /// Resolves each row's ledger growth once: the serialized <c>propGrowthMm3</c> column
        /// when present and positive, else the same formula priced here from the row's
        /// gameplay bounds, its k and the default 0.8 penalty.
        /// </summary>
        /// <remarks>
        /// The serialized column is the truth (the importer computed it in double and wrote an
        /// exact long). The fallback exists for a scene synced before the column existed, so an
        /// un-reimported lab is still playable; it is priced in float, so a huge row can differ
        /// from the imported number by a millilitre - reported once, never silently trusted as
        /// the imported value.
        /// </remarks>
        private void ResolveGrowthMm3()
        {
            growthFallbackRows = 0;
            for (int i = 0; i < propCount; i++)
            {
                if (propGrowthMm3 != null && i < propGrowthMm3.Length && propGrowthMm3[i] > 0L)
                {
                    growthMm3[i] = propGrowthMm3[i];
                    continue;
                }

                long derived;
                if (PickupAbsorbMath.TryComputeGrowthMm3(propOwnSizes[i], EffectiveVolumeK(i),
                    PickupAbsorbMath.DefaultVolumePenalty, out derived))
                {
                    growthMm3[i] = derived;
                }
                else
                {
                    growthMm3[i] = 0L;
                }
                growthFallbackRows = growthFallbackRows + 1;
            }

            if (growthFallbackRows > 0)
            {
                Debug.LogWarning("[PickupFieldHost] " + growthFallbackRows + " row(s) have no"
                    + " usable serialized propGrowthMm3; the host priced them once at Start"
                    + " from the prop's own size x k x 0.80. Re-import the level so the derived column"
                    + " is written (the imported value is exact; this fallback is float).");
            }
        }

        private bool IsSubmittable(int index)
        {
            if (propTransforms[index] == null) return false;
            // SubmitCandidate refuses an empty id and the gate refuses an empty kind text.
            if (itemIds[index] == null) return false;
            if (itemIds[index].Length == 0) return false;
            if (kinds[index] == null) return false;
            if (kinds[index].Length == 0) return false;
            // A row whose growth is not a positive mm^3 gain would be RejectNoGain at commit
            // time, which is also "stays queued": never offer it.
            if (growthMm3[index] <= 0L) return false;
            Vector3 bounds = propBounds[index];
            if (!KatamariMath.IsFiniteVector(bounds)) return false;
            if (bounds.x < 0f || bounds.y < 0f || bounds.z < 0f) return false;
            Vector3 size = propOwnSizes[index];
            if (!KatamariMath.IsFiniteVector(size)) return false;
            if (size.x < 0f || size.y < 0f || size.z < 0f) return false;
            return true;
        }

        /// <summary>
        /// Fixes which rows can ever be a prop obstacle (a transform, and a positive
        /// finite gameplay volume) and how far each one's box can reach from its centre.
        /// </summary>
        /// <remarks>
        /// <para>
        /// A row that is not submittable can still block: "the motor would refuse this
        /// candidate" (empty kind, bad value units) says nothing about the physics of an
        /// object standing in the field. Only a row with no transform and no usable volume
        /// is excluded, because there is no box to build.
        /// </para>
        /// <para>
        /// The half diagonal is the fixed part of the pruning bound in
        /// <see cref="UpdatePropObstacles"/>: for any point, the distance to an
        /// axis-aligned box is at least the distance to its centre minus its half diagonal.
        /// It depends only on propBounds, so it is computed once here.
        /// </para>
        /// </remarks>
        private void PreparePropRows()
        {
            propUsableCount = 0;
            for (int i = 0; i < propCount; i++)
            {
                propUsable[i] = false;
                propHalfDiagonal[i] = 0f;
                if (propTransforms[i] == null) continue;
                Vector3 bounds = propBounds[i];
                if (!KatamariMath.IsFiniteVector(bounds)) continue;
                // A zero-volume prop has Req 0 mm, so it can never block; requiring strictly
                // positive sizes keeps this row's box out of the maths entirely instead of
                // relying on that fact downstream.
                if (bounds.x <= 0f || bounds.y <= 0f || bounds.z <= 0f) continue;

                float halfX = bounds.x * 0.5f;
                float halfY = bounds.y * 0.5f;
                float halfZ = bounds.z * 0.5f;
                float halfDiagonal = Mathf.Sqrt(halfX * halfX + halfY * halfY + halfZ * halfZ);
                if (!KatamariMath.IsFiniteFloat(halfDiagonal)) continue;
                propHalfDiagonal[i] = halfDiagonal;
                propUsable[i] = true;
                propUsableCount = propUsableCount + 1;
            }
        }

        // ------------------------------------------------------------------
        // Spatial index: which rows can matter to the ball right now.
        // ------------------------------------------------------------------

        /// <summary>
        /// Builds, once in Start, everything the per-scan row selection reads: each row's
        /// absorb requirement, the static rows' world position and box, the grid over the
        /// static rows, and the short list of rows that are read live every scan.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Why. The old scan walked all rows three times per interval (candidate scan,
        /// obstacle classification, nearest-solid selection), each row costing a Transform
        /// read and about thirty Udon operations, which was ~3.9 ms of the Cancer level's
        /// 14 ms script time with the ball standing still. Only the rows within a couple of
        /// metres of the ball can change any decision, and a static row's position never
        /// changes, so it can be found by cell instead of by measurement.
        /// </para>
        /// <para>
        /// Row classes. <i>Mobile</i> (<c>propMobile[i]</c>, or every row when the column is
        /// absent): the Transform is read each scan, exactly as before. <i>Static</i>: the
        /// position and the blocking box are computed here once. A static row with a box
        /// reaching further than <see cref="GridMaxHalfDiagonalM"/> from its centre goes to the
        /// linear list (it would stretch every grid query) but still uses its cached values.
        /// Rows with no transform, or neither a candidate nor an obstacle role, are in no list:
        /// no stage ever acted on them. The result of every stage is identical to the full
        /// walk; only the set of rows it looks at shrinks.
        /// </para>
        /// </remarks>
        private void BuildSpatialIndex()
        {
            rowMobile = new bool[propCount];
            rowTracked = new bool[propCount];
            rowInLinear = new bool[propCount];
            propBoxValid = new bool[propCount];
            propWorldPositions = new Vector3[propCount];
            propReqMm = new float[propCount];
            linearRows = new int[propCount];
            staticRows = new int[propCount];
            queryRows = new int[propCount];
            nearRows = new int[propCount];
            nearDistSq = new float[propCount];
            nearPositions = new Vector3[propCount];
            candRows = new int[propCount];
            candDistanceByRow = new float[propCount];
            candCount = 0;
            nearCount = 0;
            nearValid = false;
            linearCount = 0;
            staticCount = 0;
            driftCursor = 0;
            driftLogCount = 0;
            mobileRowCount = 0;
            bigStaticRowCount = 0;
            gridActive = false;
            gridMemberCount = 0;
            gridMaxHalfDiagonal = 0f;

            bool haveColumn = propMobile != null && propMobile.Length >= propCount;
            bool[] member = new bool[propCount];
            int memberCount = 0;
            for (int i = 0; i < propCount; i++)
            {
                propReqMm[i] = PickupAbsorbMath.ResolveRequirementMmOrInvalid(propOwnSizes[i],
                    propGateK[i]);
                Transform t = propTransforms[i];
                if (t == null) continue;
                if (!submittable[i] && !propUsable[i]) continue;

                Vector3 position = t.position;
                propWorldPositions[i] = position;
                bool mobile = !haveColumn || propMobile[i];
                // A static row that is not at a finite place was skipped by every stage.
                if (!mobile && !KatamariMath.IsFiniteVector(position)) continue;
                rowTracked[i] = true;

                if (mobile)
                {
                    rowMobile[i] = true;
                    rowInLinear[i] = true;
                    linearRows[linearCount] = i;
                    linearCount = linearCount + 1;
                    mobileRowCount = mobileRowCount + 1;
                    continue;
                }

                staticRows[staticCount] = i;
                staticCount = staticCount + 1;
                if (propUsable[i])
                {
                    Vector3 minimum;
                    Vector3 maximum;
                    propBoxValid[i] = BlockingPropMath.TryBoxFromCentreAndExtents(position,
                        propBounds[i], out minimum, out maximum);
                    if (propBoxValid[i])
                    {
                        propMinima[i] = minimum;
                        propMaxima[i] = maximum;
                    }
                    if (propHalfDiagonal[i] > GridMaxHalfDiagonalM)
                    {
                        rowInLinear[i] = true;
                        linearRows[linearCount] = i;
                        linearCount = linearCount + 1;
                        bigStaticRowCount = bigStaticRowCount + 1;
                        continue;
                    }
                    if (propHalfDiagonal[i] > gridMaxHalfDiagonal)
                        gridMaxHalfDiagonal = propHalfDiagonal[i];
                }
                member[i] = true;
                memberCount = memberCount + 1;
            }

            if (memberCount > 0)
            {
                gridInfo = new float[PickupSpatialGrid.InfoLength];
                if (PickupSpatialGrid.TryComputeLayout(propWorldPositions, member, propCount,
                    gridCellSizeM, GridMaxCells, gridInfo))
                {
                    int cells = PickupSpatialGrid.CellCount(gridInfo);
                    gridCellStart = new int[cells + 1];
                    gridCellRows = new int[memberCount];
                    gridCellMin = new Vector3[cells];
                    gridCellMax = new Vector3[cells];
                    gridActive = PickupSpatialGrid.BuildCells(gridInfo, propWorldPositions, member,
                        propCount, gridCellStart, gridCellRows)
                        && PickupSpatialGrid.BuildCellBounds(gridInfo, propWorldPositions,
                        gridCellStart, gridCellRows, gridCellMin, gridCellMax);
                }
                if (gridActive)
                {
                    gridMemberCount = memberCount;
                }
                else
                {
                    // The grid could not be built: the static rows are scanned like the rest.
                    for (int i = 0; i < propCount; i++)
                    {
                        if (!member[i]) continue;
                        rowInLinear[i] = true;
                        linearRows[linearCount] = i;
                        linearCount = linearCount + 1;
                    }
                }
            }
        }

        /// <summary>
        /// Marks a row as one whose Transform another system moves, so every scan reads it
        /// live from now on. Idempotent; a row the index never tracked, a bad index and a
        /// call before Start are ignored.
        /// </summary>
        /// <remarks>
        /// The contract for movers: a system that writes a prop's Transform calls this before
        /// its first write (the stack fall does), or the level's <c>propMobile</c> column names
        /// the row. The mover keeps writing the prop's own Transform - the scan reads that, so
        /// a mover that updates it less often just shows the scan the position it last wrote,
        /// which is what the full walk read too.
        /// </remarks>
        public void MarkPropMobile(int index)
        {
            if (rowMobile == null || index < 0 || index >= propCount) return;
            if (rowMobile[index]) return;
            if (!rowTracked[index]) return;
            rowMobile[index] = true;
            mobileRowCount = mobileRowCount + 1;
            if (rowInLinear[index]) return;
            rowInLinear[index] = true;
            linearRows[linearCount] = index;
            linearCount = linearCount + 1;
        }

        /// <summary>
        /// Safety net for the static-row cache: re-reads a few static rows per frame and
        /// turns a row that is not where Start saw it into a mobile row, with one warning.
        /// </summary>
        /// <remarks>
        /// A mover that neither the <c>propMobile</c> column nor <see cref="MarkPropMobile"/>
        /// named is found within <c>staticCount / DriftChecksPerFrame</c> frames; until then the
        /// scan sees its old position. The warning names the row so the column can be fixed.
        /// </remarks>
        private void VerifyStaticRows()
        {
            if (staticCount <= 0) return;
            for (int n = 0; n < DriftChecksPerFrame; n++)
            {
                driftCursor = driftCursor + 1;
                if (driftCursor >= staticCount) driftCursor = 0;
                int row = staticRows[driftCursor];
                if (rowMobile[row]) continue;
                if (propStates[row] != StateFree) continue;

                Vector3 live = propTransforms[row].position;
                Vector3 cached = propWorldPositions[row];
                if (Mathf.Abs(live.x - cached.x) <= DriftToleranceM
                    && Mathf.Abs(live.y - cached.y) <= DriftToleranceM
                    && Mathf.Abs(live.z - cached.z) <= DriftToleranceM) continue;

                MarkPropMobile(row);
                if (driftLogCount < DriftLogLimit)
                {
                    driftLogCount = driftLogCount + 1;
                    Debug.LogWarning("[PickupFieldHost] static row " + row + " (" + itemIds[row]
                        + ") moved " + Vector3.Distance(live, cached).ToString("F3")
                        + " m without being marked mobile; it is read live from now on."
                        + " Fix the propMobile column (re-run Sync Prop Arrays) or call"
                        + " MarkPropMobile before moving it.");
                }
            }
        }

        // Gathers the free rows near the ball unless this Update already did for at least
        // these reaches. A negative reach means "that stage is not running".
        private void EnsureNearRows(Vector3 center, float scanReachM, float pruneReachM)
        {
            if (nearValid && nearScanReachM >= scanReachM && nearPruneReachM >= pruneReachM)
                return;
            GatherNearRows(center, scanReachM, pruneReachM);
        }

        /// <summary>
        /// Fills nearRows / nearDistSq / nearPositions with the free rows that the candidate
        /// scan (reach <paramref name="scanReachM"/> from the ball centre) or the obstacle
        /// stage (reach <paramref name="pruneReachM"/> plus the row's half diagonal) could act
        /// on. A superset of what each stage keeps; the stages apply their own exact tests.
        /// </summary>
        /// <remarks>
        /// The squared distances and the limits are computed with the same expressions the
        /// stages use, from the same positions (a static row's cached one equals what its
        /// Transform would read), so a row the stage would keep is never dropped here.
        /// </remarks>
        private void GatherNearRows(Vector3 center, float scanReachM, float pruneReachM)
        {
            bool profile = performanceDiagnostics != null && performanceDiagnostics.sampling;
            long scanStart = profile ? BallPerformanceMath.Timestamp() : 0L;
            int total = 0;
            float gridReach = scanReachM;
            if (pruneReachM >= 0f)
            {
                float withExtent = pruneReachM + gridMaxHalfDiagonal;
                if (withExtent > gridReach) gridReach = withExtent;
            }
            if (gridActive && gridReach >= 0f)
            {
                total = PickupSpatialGrid.Query(center, gridReach, gridInfo, gridCellStart,
                    gridCellRows, gridCellMin, gridCellMax, queryRows);
                if (total < 0)
                {
                    // Cannot happen with a finite centre and a built grid; if it ever does,
                    // look at every grid row rather than none.
                    total = 0;
                    for (int g = 0; g < gridMemberCount; g++)
                    {
                        queryRows[total] = gridCellRows[g];
                        total = total + 1;
                    }
                }
            }
            for (int k = 0; k < linearCount; k++)
            {
                queryRows[total] = linearRows[k];
                total = total + 1;
            }

            float scanSq = -1f;
            if (scanReachM >= 0f) scanSq = scanReachM * scanReachM;
            int near = 0;
            for (int k = 0; k < total; k++)
            {
                int row = queryRows[k];
                if (propStates[row] != StateFree) continue;
                Vector3 position;
                if (rowMobile[row]) position = propTransforms[row].position;
                else position = propWorldPositions[row];

                float dx = position.x - center.x;
                float dy = position.y - center.y;
                float dz = position.z - center.z;
                float distanceSq = dx * dx + dy * dy + dz * dz;
                if (!KatamariMath.IsFiniteFloat(distanceSq)) continue;

                float limitSq = scanSq;
                if (pruneReachM >= 0f)
                {
                    float pruneLimit = pruneReachM + propHalfDiagonal[row];
                    float pruneSq = pruneLimit * pruneLimit;
                    if (pruneSq > limitSq) limitSq = pruneSq;
                }
                if (distanceSq > limitSq) continue;

                nearRows[near] = row;
                nearDistSq[near] = distanceSq;
                nearPositions[near] = position;
                near = near + 1;
            }
            nearCount = near;
            nearScanReachM = scanReachM;
            nearPruneReachM = pruneReachM;
            nearValid = true;
            if (profile) BallPerformanceMath.Split(performanceDiagnostics, BallPerformanceMath.PickupScan, scanStart);
        }

        /// <summary>
        /// Reports the authoring mistakes this layer can see, once each, in one line per
        /// category instead of one line per prop.
        /// </summary>
        private void ReportPropDataProblems()
        {
            int missingTransforms = 0;
            int emptyIds = 0;
            int emptyKinds = 0;
            int badGrowth = 0;
            int degenerateBounds = 0;
            for (int i = 0; i < propCount; i++)
            {
                if (propTransforms[i] == null) missingTransforms++;
                if (itemIds[i] == null || itemIds[i].Length == 0) emptyIds++;
                if (kinds[i] == null || kinds[i].Length == 0) emptyKinds++;
                if (growthMm3[i] <= 0L) badGrowth++;
                Vector3 bounds = propBounds[i];
                if (!KatamariMath.IsFiniteVector(bounds)) degenerateBounds++;
                else if (bounds.x <= 0f || bounds.y <= 0f || bounds.z <= 0f) degenerateBounds++;
            }

            int longest = 0;
            if (propTransforms != null) longest = Larger(longest, propTransforms.Length);
            if (itemIds != null) longest = Larger(longest, itemIds.Length);
            if (kinds != null) longest = Larger(longest, kinds.Length);
            if (propBounds != null) longest = Larger(longest, propBounds.Length);
            if (propGrowthMm3 != null) longest = Larger(longest, propGrowthMm3.Length);
            int ignored = longest - propCount;

            if (propTransforms == null || itemIds == null || kinds == null ||
                propBounds == null)
            {
                Debug.LogWarning("[PickupFieldHost] a prop array is null; props=" + propCount);
            }
            else if (ignored > 0)
            {
                Debug.LogWarning("[PickupFieldHost] parallel prop arrays disagree; ignoring the "
                    + ignored + " trailing entr(y/ies) of the longer arrays. props=" + propCount);
            }
            if (missingTransforms > 0 || emptyIds > 0 || emptyKinds > 0 ||
                badGrowth > 0 || degenerateBounds > 0)
            {
                Debug.LogWarning("[PickupFieldHost] prop data: missingTransforms=" + missingTransforms
                    + " emptyIds=" + emptyIds
                    + " emptyKinds=" + emptyKinds
                    + " growthMm3NotPositive=" + badGrowth
                    + " zeroOrNegativeBounds=" + degenerateBounds
                    + " - these rows are never offered to the motor, so they can never be"
                    + " absorbed (growthMm3 must be a positive number of mm^3; the column is"
                    + " derived at import as round(penalty*k*a*b*c*1e9), so a non-positive value"
                    + " means bad bounds/k/penalty data - see"
                    + " Docs/Research/ContentVolume/FIXED_POINT_STEP_CD.md)");
            }
            CheckPropColliderWiring();
        }

        private void CheckPropColliderWiring()
        {
            if (loggedNoColliders || decorativeOnly) return;
            int missing = 0;
            int first = -1;
            for (int i = 0; i < propCount; i++)
            {
                if (propColliders != null && i < propColliders.Length && propColliders[i] != null)
                    continue;
                Transform prop = propTransforms[i];
                // Match the authoring search scope (the root and inactive descendants),
                // only once at initialization and only for rows without a reference.
                if (prop == null || prop.GetComponentInChildren<Collider>(true) == null) continue;
                if (first < 0) first = i;
                missing++;
            }
            if (missing == 0) return;
            loggedNoColliders = true;
            Debug.LogWarning("[PickupFieldHost] propColliders is missing " + missing + "/" + propCount
                + " reference(s) for props with Unity Colliders; their colliders will not be disabled"
                + " on attach. First row=" + first + " id=" + itemIds[first]);
        }

        /// <summary>
        /// Resolves every row's own size once - <c>propLocalBounds</c> when usable, else the
        /// world AABB - and reports, once, how many rows fell back and why.
        /// </summary>
        /// <remarks>
        /// The absorb gate, the blocking predicate, the growth fallback and the radial proxy
        /// all need the prop's size along ITS OWN axes; the world AABB of a turned prop is
        /// larger than the prop and would price it too high. The fallback is deliberate for a
        /// scene that was never re-synced after the local column was added: refusing the prop
        /// would be worse than the old behaviour (wrong only for a turned prop). The warning
        /// names the array problem so a re-sync is the obvious fix. The reason codes are the
        /// ones <see cref="RadialProxyShellMath.TryResolvePropExtents"/> reports; printing the
        /// number as well as the text keeps the line greppable.
        /// </remarks>
        private void ResolveOwnSizes()
        {
            ownSizeFallbackRows = 0;
            if (propCount <= 0) return;
            int expectedRows = itemIds == null ? 0 : itemIds.Length;
            int reasonCode = RadialProxyShellMath.ExtentsSourceLocal;
            for (int i = 0; i < propCount; i++)
            {
                Vector3 extents;
                int source;
                if (!RadialProxyShellMath.TryResolvePropExtents(propLocalBounds, propBounds, i,
                        expectedRows, out extents, out source))
                {
                    // The row is outside one of the two arrays; treat it as a bad local row
                    // rather than silently trusting a value this loop could not read.
                    extents = propBounds[i];
                    source = RadialProxyShellMath.ExtentsSourceWorldRowInvalid;
                }
                propOwnSizes[i] = extents;
                if (source == RadialProxyShellMath.ExtentsSourceLocal) continue;
                ownSizeFallbackRows = ownSizeFallbackRows + 1;
                if (reasonCode == RadialProxyShellMath.ExtentsSourceLocal) reasonCode = source;
            }
            if (ownSizeFallbackRows == 0) return;
            Debug.LogWarning("[PickupFieldHost] prop own-size fallback reason=" + reasonCode
                + " (" + OwnSizeFallbackReasonText(reasonCode) + ")"
                + " rows=" + ownSizeFallbackRows + "/" + propCount
                + " localBoundsRows=" + (propLocalBounds == null ? 0 : propLocalBounds.Length)
                + " expectedRows=" + expectedRows
                + " - these rows use propBounds (world AABB) as the prop's size, so a turned"
                + " prop's absorb requirement is priced too high and its radial proxy box is"
                + " rotated twice. Re-run VRC Katamari/Pickup/Sync Prop Arrays From Children.");
        }

        private string OwnSizeFallbackReasonText(int reasonCode)
        {
            if (reasonCode == RadialProxyShellMath.ExtentsSourceWorldMissing)
                return "propLocalBounds is missing (the scene predates the column; re-sync it)";
            if (reasonCode == RadialProxyShellMath.ExtentsSourceWorldLengthMismatch)
                return "propLocalBounds length is not itemIds length (re-sync the arrays)";
            if (reasonCode == RadialProxyShellMath.ExtentsSourceWorldRowInvalid)
                return "propLocalBounds row is not a finite non-negative size (re-sync the arrays)";
            return "none";
        }

        // ------------------------------------------------------------------
        // Ring consumption: absolute cursor, one event per committed absorb.
        // ------------------------------------------------------------------

        private void ConsumeAbsorbedEvents()
        {
            int total = motor.GetAbsorbedEventCount();
            if (total == seenEvents) return;
            if (total < seenEvents)
            {
                // A round reset took the ring backwards; resync instead of reading slots that
                // now describe a different event.
                seenEvents = total;
                if (absorbFeedback != null) absorbFeedback.ClearFeedback();
                return;
            }

            int capacity = motor.GetAbsorbedEventCapacity();
            if (capacity <= 0)
            {
                seenEvents = total;
                return;
            }

            int advanced = total - seenEvents;
            if (advanced > capacity && !loggedLostEvents)
            {
                // Honest reporting of the ring's own limit: the reader fell further behind
                // than the ring holds, so that many events are gone and their props stay
                // unattached (visible, but not stuck to the ball).
                loggedLostEvents = true;
                Debug.LogWarning("[PickupFieldHost] lost " + (advanced - capacity)
                    + " absorbed event(s): the ring holds " + capacity
                    + " and the cursor was " + advanced + " behind. Those props stay unattached.");
            }

            int first = seenEvents;
            int oldestRetained = total - capacity;
            if (first < oldestRetained) first = oldestRetained;

            for (int i = first; i < total; i++)
            {
                string absorbedId = motor.GetAbsorbedEventItemId(i);
                if (absorbedId == null) continue;
                if (absorbedId.Length == 0) continue;
                int index = FindPropIndex(absorbedId);
                if (index < 0) continue;
                AttachProp(index, i);
                if (fallHost != null) fallHost.OnPropAbsorbed(index);
                // The prop is gone from the world, so its own collision goes with it: no
                // invisible wall where a rolled-up lantern stood.
                if (arenaHost != null) arenaHost.RemoveSolidsOf(absorbedId);
                if (absorbFeedback != null && Collecting())
                {
                    int screamId = GetPropAbsorbSoundClass(index);
                    Vector3 size = GetPropOwnSize(index);
                    float edge = Mathf.Pow(size.x * size.y * size.z, 1f / 3f);
                    absorbFeedback.RecordConfirmed(screamId, edge / motor.GetSemanticDiameterWorldM());
                }
                if (catchNameDisplay != null && Collecting())
                    catchNameDisplay.RecordCatch(propTransforms[index], CatchName(propCatchNamesZh, index),
                        CatchName(propCatchNamesEn, index), CatchName(propCatchNamesJa, index), 0);
            }
            seenEvents = total;
        }

        /// <summary>Index of the prop with this item id, or -1 when it is not this host's.</summary>
        private int FindPropIndex(string itemId)
        {
            // An absorbed prop was one of this host's queued candidates, so look at those few
            // first (item ids are unique per field: the authoring rules refuse duplicates, so
            // this finds the same row the full walk would).
            for (int q = 0; q < queuedCount; q++)
            {
                int queuedRow = queuedIndices[q];
                if (itemIds[queuedRow] == itemId) return queuedRow;
            }
            for (int i = 0; i < propCount; i++)
            {
                if (itemIds[i] == itemId) return i;
            }
            return -1;
        }

        // ------------------------------------------------------------------
        // Attach: ball-frame local pose, then parent under the attachment root.
        // ------------------------------------------------------------------

        private void AttachProp(int index, int eventIndex)
        {
            if (propStates[index] != StateFree) return;
            Transform prop = propTransforms[index];
            if (prop == null)
            {
                HideProp(index, HiddenReasonNoTransform);
                return;
            }
            if (attachmentRoot == null)
            {
                HideProp(index, HiddenReasonNoRoot);
                return;
            }

            Vector3 worldScale = prop.lossyScale;
            if (!AttachmentMath.IsValidObjectScale(worldScale))
            {
                HideProp(index, HiddenReasonScaleInvalid);
                return;
            }

            // The event already contains the local contact pose from the accepting step.
            // Neither the ball nor a moving/free prop's current world pose can reconstruct
            // that pose here: the motor may have run several more fixed steps in Update.
            Vector3 localPosition;
            Quaternion localRotation;
            Vector3 contactCenter;
            float contactCollisionRadius;
            float contactRadius;
            int delaySteps;
            if (!motor.TryGetAbsorbedEventAttachment(eventIndex, out localPosition,
                out localRotation, out contactCenter, out contactCollisionRadius,
                out contactRadius, out delaySteps))
            {
                HideProp(index, HiddenReasonPoseRefused);
                return;
            }

            // Measure the pose actually assigned below, in the ball's rigid local frame.
            // A sphere is rotation-invariant; the event's pre-growth radius and margin
            // make this directly comparable with the motor line for the SAME event.
            string boxGapText = "n/a";
            float boxGap;
            if (AttachmentMath.TryComputeBoxGap(propOwnSizes[index], localRotation,
                localPosition, Vector3.zero, contactCollisionRadius, contactRadius, out boxGap))
            {
                boxGapText = (boxGap * 1000f).ToString("F1");
            }

            prop.SetParent(attachmentRoot);
            prop.localPosition = localPosition;
            prop.localRotation = localRotation;
            if (propColliders != null && index < propColliders.Length && propColliders[index] != null)
                propColliders[index].enabled = false;

            // The shell wants the prop's size along ITS OWN local axes, in the same
            // ball-local pose the visual prop just took. localRotation already contains the
            // prop's own rotation, so passing the world AABB (propBounds) would turn a yawed
            // prop twice. EVERY attached prop goes to the motor, which lengthens the ball's
            // 16 pins with its box (a prop buried in the core is skipped there); this host
            // owns no proxy state.
            motor.AttachRadialProxy(itemIds[index], propOwnSizes[index], localPosition,
                localRotation);

            propStates[index] = StateAttached;
            attachedCount = attachedCount + 1;
            RegisterDisplay(index, localPosition, localRotation);
            ForgetQueued(index);
            // The prop may have been supporting the ball or blocking it a moment ago. The
            // obstacle refresh runs later in this same Update, before the collecting gate,
            // so pulling the next scan forward makes the absorbed prop lose both roles in
            // the frame the event is consumed instead of up to one scan later.
            nextBlockingScanTime = 0f;
            if (objectiveKinds == null || objectiveKinds.Length == 0) TallyKind(kinds[index]);
            else if (index < objectiveKinds.Length) TallyKind(objectiveKinds[index]);
            Vector3 ballCenter = motor.GetCenterWorld();
            CheckAttachmentRootOnce(ballCenter);
            Debug.Log("[PickupFieldHost] attached id=" + itemIds[index]
                + " kind=" + kinds[index] + " event=" + eventIndex
                + " boxGapMm=" + boxGapText
                + " ballR=" + contactCollisionRadius
                + " contactMm=" + (contactRadius * 1000f).ToString("F1")
                + " delaySteps=" + delaySteps
                + " centerDeltaMm=" + (Vector3.Distance(ballCenter, contactCenter) * 1000f).ToString("F1")
                + " attached=" + attachedCount
                + " displayed=" + displayShownCount
                + " buried=" + buriedCount);
        }

        // ------------------------------------------------------------------
        // Attached display: bury first, then draw the biggest N + most recent N
        // (AttachedDisplayMath). Only renderers are switched; parent, pose, ledger,
        // volume, collision radius and the 16-pin shell are untouched.
        // ------------------------------------------------------------------

        // Every array the display layer reads, allocated together in AllocatePropStore and
        // sized to the prop count.
        private bool DisplayStoreReady()
        {
            return displaySeq != null && displayKey != null && displayBuried != null
                && displayFarMm != null && displayHist != null && displaySorted != null
                && displayBig != null && displayRecent != null && displayBigBefore != null
                && displayRecentBefore != null && displayCorners != null
                && displayRenderers != null && displayRendererWasEnabled != null
                && displayRendererStart != null && displayRendererLength != null
                && propOwnSizes != null && propTransforms != null;
        }

        private bool DisplayRowReady(int index)
        {
            if (!DisplayStoreReady()) return false;
            return index >= 0 && index < displaySeq.Length && index < displayRendererStart.Length
                && index < propOwnSizes.Length && index < propTransforms.Length;
        }

        private void ClearDisplayState()
        {
            AttachedDisplayMath.ResetRows(displaySeq, displayBuried, propCount);
            displayHistCount = 0;
            displaySortedCount = 0;
            displayBuryCursor = 0;
            displayBigCount = 0;
            displayRecentCount = 0;
            displayRendererCount = 0;
            displayShownCount = 0;
            buriedCount = 0;
            displayCheckedRadiusMm = -1;
            displayCheckedAttached = -1;
        }

        /// <summary>
        /// Records the prop's renderers, decides whether it is already buried, and lets the
        /// display cap show it (and hide at most one other prop).
        /// </summary>
        private void RegisterDisplay(int index, Vector3 localPosition, Quaternion localRotation)
        {
            // The display layer is optional for the attach itself: without its store (a host
            // whose Start never allocated it) the prop simply stays drawn as before.
            if (!DisplayRowReady(index)) return;
            RecordDisplayRenderers(index);
            // Hidden until the cap says otherwise; a refused registration keeps the prop drawn
            // (nothing is lost, the cap just does not cover it).
            SetDisplayRenderers(index, false);

            float farthest;
            int farMm = 0;
            if (!RadialProxyShellMath.TryBuildAabbCorners(localPosition, localRotation,
                propOwnSizes[index], displayCorners, out farthest)
                || !AttachedDisplayMath.TryFloorMm(farthest, out farMm))
            {
                SetDisplayRenderers(index, true);
                return;
            }
            displayFarMm[index] = farMm;
            displayKey[index] = AttachedDisplayMath.VolumeKey(propOwnSizes[index]);

            int buryMm;
            bool buried = TryCurrentBuryRadiusMm(out buryMm) && farMm < buryMm;
            int shown;
            int hidden;
            if (!AttachedDisplayMath.TryAttach(index, buried, displayKey, displaySeq, displayBuried,
                displayHist, ref displayHistCount, displayBig, ref displayBigCount, displayBiggestCap,
                displayRecent, ref displayRecentCount, displayRecentCap,
                out shown, out hidden))
            {
                SetDisplayRenderers(index, true);
                return;
            }
            if (buried)
            {
                buriedCount = buriedCount + 1;
                return;
            }
            AttachedDisplayMath.TryInsertByFar(index, displayFarMm, displaySorted,
                ref displaySortedCount, displayBuryCursor);
            if (shown >= 0)
            {
                SetDisplayRenderers(shown, true);
                displayShownCount = displayShownCount + 1;
            }
            if (hidden >= 0)
            {
                SetDisplayRenderers(hidden, false);
                displayShownCount = displayShownCount - 1;
            }
        }

        /// <summary>
        /// Buries the props that now lie wholly inside the bury sphere. Runs only when the
        /// collision radius (whole mm) or the attached count changed; the cursor only moves
        /// forward, so a bank shrink buries nothing new and brings nothing back. A frame that
        /// buries a shown prop rebuilds the two groups once.
        /// </summary>
        private void UpdateBuried()
        {
            if (!DisplayStoreReady() || motor == null) return;
            int radiusMm;
            if (!AttachedDisplayMath.TryFloorMm(motor.GetCollisionRadiusWorldM(), out radiusMm)) return;
            if (radiusMm == displayCheckedRadiusMm && attachedCount == displayCheckedAttached) return;
            displayCheckedRadiusMm = radiusMm;
            displayCheckedAttached = attachedCount;

            int buryMm;
            if (!TryCurrentBuryRadiusMm(out buryMm)) return;
            bool rebuild = false;
            int row = AttachedDisplayMath.NextToBury(displayFarMm, displaySorted,
                displaySortedCount, ref displayBuryCursor, buryMm);
            while (row >= 0)
            {
                if (AttachedDisplayMath.MarkBuried(row, displaySeq, displayBuried, displayBig,
                    displayBigCount, displayRecent, displayRecentCount)) rebuild = true;
                buriedCount = buriedCount + 1;
                row = AttachedDisplayMath.NextToBury(displayFarMm, displaySorted,
                    displaySortedCount, ref displayBuryCursor, buryMm);
            }
            // At most one rebuild per call, and this call runs only when the radius or the
            // attached count changed: a burst of burials costs one O(n x (N + N)) pass.
            if (rebuild) RebuildDisplay();
        }

        // One full recompute of the groups, then renderers switched only where membership
        // changed (the before/after diff over at most bigCap + recentCap rows each).
        private void RebuildDisplay()
        {
            int bigBefore = displayBigCount;
            int recentBefore = displayRecentCount;
            for (int i = 0; i < bigBefore; i++) displayBigBefore[i] = displayBig[i];
            for (int i = 0; i < recentBefore; i++) displayRecentBefore[i] = displayRecent[i];
            if (!AttachedDisplayMath.TryRebuild(displayKey, displaySeq, displayBuried, displayHist,
                displayHistCount, displayBig, ref displayBigCount, displayBiggestCap,
                displayRecent, ref displayRecentCount, displayRecentCap)) return;
            for (int i = 0; i < bigBefore; i++) SyncDisplayRow(displayBigBefore[i]);
            for (int i = 0; i < recentBefore; i++) SyncDisplayRow(displayRecentBefore[i]);
            for (int i = 0; i < displayBigCount; i++) SyncDisplayRow(displayBig[i]);
            for (int i = 0; i < displayRecentCount; i++) SyncDisplayRow(displayRecent[i]);
            displayShownCount = displayBigCount + displayRecentCount;
        }

        private void SyncDisplayRow(int row)
        {
            SetDisplayRenderers(row, AttachedDisplayMath.IsShown(row, displayBig, displayBigCount,
                displayRecent, displayRecentCount));
        }

        private bool TryCurrentBuryRadiusMm(out int buryMm)
        {
            buryMm = 0;
            if (motor == null) return false;
            return AttachedDisplayMath.TryBuryRadiusMm(motor.GetCollisionRadiusWorldM(),
                0.5f * motor.GetStartDiameterWorldM() * CollisionPerSemanticDiameter(),
                attachedCount, out buryMm);
        }

        // The start diameter is semantic; the bury sphere is in collision radii. Both come
        // from the motor's own size ratio, measured instead of re-derived.
        private float CollisionPerSemanticDiameter()
        {
            float semantic = motor.GetSemanticDiameterWorldM();
            float collision = motor.GetCollisionRadiusWorldM();
            if (!KatamariMath.IsFiniteFloat(semantic) || semantic <= 0f) return 1f;
            if (!KatamariMath.IsFiniteFloat(collision) || collision <= 0f) return 1f;
            return 2f * collision / semantic;
        }

        private void RecordDisplayRenderers(int index)
        {
            displayRendererStart[index] = displayRendererCount;
            displayRendererLength[index] = 0;
            Transform prop = propTransforms[index];
            if (prop == null) return;
            Renderer[] found = prop.GetComponentsInChildren<Renderer>(true);
            if (found == null || found.Length == 0) return;
            if (displayRendererCount + found.Length > displayRenderers.Length)
            {
                int capacity = displayRenderers.Length * 2;
                while (capacity < displayRendererCount + found.Length) capacity = capacity * 2;
                Renderer[] grownRenderers = new Renderer[capacity];
                bool[] grownEnabled = new bool[capacity];
                for (int i = 0; i < displayRendererCount; i++)
                {
                    grownRenderers[i] = displayRenderers[i];
                    grownEnabled[i] = displayRendererWasEnabled[i];
                }
                displayRenderers = grownRenderers;
                displayRendererWasEnabled = grownEnabled;
            }
            for (int i = 0; i < found.Length; i++)
            {
                displayRenderers[displayRendererCount] = found[i];
                displayRendererWasEnabled[displayRendererCount] = found[i] != null && found[i].enabled;
                displayRendererCount = displayRendererCount + 1;
            }
            displayRendererLength[index] = found.Length;
        }

        // Shown = each renderer back to the flag it had when the prop attached (a skeletal
        // prop's static mesh stays off); hidden = every renderer off.
        private void SetDisplayRenderers(int index, bool shown)
        {
            if (!DisplayRowReady(index)) return;
            int start = displayRendererStart[index];
            int end = start + displayRendererLength[index];
            for (int i = start; i < end; i++)
            {
                Renderer renderer = displayRenderers[i];
                if (renderer == null) continue;
                bool want = shown && displayRendererWasEnabled[i];
                if (renderer.enabled != want) renderer.enabled = want;
            }
        }

        private void RestoreDisplayRenderers()
        {
            if (!DisplayStoreReady()) return;
            for (int i = 0; i < displayRendererCount; i++)
            {
                Renderer renderer = displayRenderers[i];
                if (renderer == null) continue;
                if (renderer.enabled != displayRendererWasEnabled[i])
                    renderer.enabled = displayRendererWasEnabled[i];
                displayRenderers[i] = null;
            }
            ClearDisplayState();
        }

        /// <summary>
        /// Hides an absorbed prop the host could not attach, and records why.
        /// </summary>
        /// <remarks>
        /// Last resort only: attachment is what S3 asks for, and hiding is still better than
        /// a prop that was eaten (ledger committed, size gained) yet keeps standing in the
        /// world. Terminal like an attach: the prop is never offered to the queue again.
        /// </remarks>
        private void HideProp(int index, int reason)
        {
            propStates[index] = StateHidden;
            ForgetQueued(index);
            // Same reason as AttachProp: a hidden prop must lose its wall/floor role in
            // the scan that follows this event, not one scan interval later.
            nextBlockingScanTime = 0f;
            hiddenCount = hiddenCount + 1;
            lastHiddenReason = reason;
            lastHiddenItemId = itemIds[index];
            if (propColliders != null && index < propColliders.Length && propColliders[index] != null)
                propColliders[index].enabled = false;
            if (hiddenLogCount < HiddenLogLimit)
            {
                hiddenLogCount = hiddenLogCount + 1;
                Debug.LogWarning("[PickupFieldHost] hid id=" + lastHiddenItemId
                    + " reason=" + HiddenReasonText(reason)
                    + " hidden=" + hiddenCount
                    + (hiddenLogCount == HiddenLogLimit ? " (further hides are silent)" : ""));
            }
            Transform prop = propTransforms[index];
            if (prop != null) prop.gameObject.SetActive(false);
        }

        private string HiddenReasonText(int reason)
        {
            if (reason == HiddenReasonPoseRefused) return "world-pose-to-local refused";
            if (reason == HiddenReasonScaleInvalid) return "non-positive or non-finite scale";
            if (reason == HiddenReasonNoRoot) return "attachmentRoot not wired";
            if (reason == HiddenReasonNoTransform) return "prop Transform not wired";
            return "unknown";
        }

        private void CheckAttachmentRootOnce(Vector3 center)
        {
            if (loggedRootMismatch) return;
            if (attachmentRoot == null) return;
            if (!KatamariMath.IsFiniteVector(center)) return;
            float offset = Vector3.Distance(attachmentRoot.position, center);
            if (!KatamariMath.IsFiniteFloat(offset)) return;
            if (offset <= AttachmentRootToleranceM) return;
            loggedRootMismatch = true;
            Debug.LogWarning("[PickupFieldHost] attachmentRoot is " + offset
                + " m from the ball centre (tolerance " + AttachmentRootToleranceM
                + " m). Props will stick to the root, not the ball: place it under"
                + " BallVisualRollRoot with identity local position and scale 1.");
        }

        private void TallyKind(string kind)
        {
            if (string.IsNullOrEmpty(kind)) return;
            for (int i = 0; i < kindTallyCount; i++)
            {
                if (kindTallyNames[i] == kind)
                {
                    kindTallyCounts[i] = kindTallyCounts[i] + 1;
                    return;
                }
            }
            if (kindTallyCount >= kindTallyNames.Length) return;
            kindTallyNames[kindTallyCount] = kind;
            kindTallyCounts[kindTallyCount] = 1;
            kindTallyCount = kindTallyCount + 1;
        }

        // ------------------------------------------------------------------
        // One proximity scan: choose the nearest absorbable props, make the queue match.
        // ------------------------------------------------------------------

        /// <summary>
        /// Chooses the nearest free props the ball can absorb at its current size, withdraws
        /// the queued props of this host that are not among them, and queues the chosen ones.
        /// </summary>
        /// <remarks>
        /// <para>
        /// One pass over the props does the choosing, so the cost per scan is one distance
        /// per prop; the size test (the absorb gate's own
        /// <see cref="PickupAbsorbMath.IsBeyondAbsorbLimit"/>, inside
        /// <see cref="PickupCandidateSelection.OfferProp"/>) runs only for the few props
        /// inside the activation radius. Absorbability, not just proximity, decides who gets
        /// one of the motor's four slots: a prop the ball is too small for would be refused
        /// on every step and, never leaving the queue by itself, would keep the small props
        /// the ball is actually rolling over out of it. Those big props are not forgotten -
        /// <see cref="UpdatePropObstacles"/> runs the same predicate and makes them walls or
        /// steps, a separate path that never reads the queue - and the next scan after the
        /// ball grows past their requirement selects them like any other prop.
        /// </para>
        /// <para>
        /// Order matters for the queue: withdraw first, then submit, so the freed slots are
        /// available to the nearer props in the same scan. Submitting into a full queue is
        /// refused and latches the motor's overflow signal, which means "the host lost a
        /// candidate", so the slot count is checked before every submit. Held-accepted
        /// candidates (see <c>WithdrawCandidateUnlessAccepted</c>) keep their slot until the
        /// commit takes them, which happens within the motor's own step.
        /// </para>
        /// </remarks>
        private void ScanProps()
        {
            float now = Time.time;
            if (now < nextScanTime) return;
            nextScanTime = now + scanIntervalSeconds;

            Vector3 center = motor.GetCenterWorld();
            if (!KatamariMath.IsFiniteVector(center)) return;

            int slotCount = motor.GetPendingCandidateCapacity();
            if (slotCount > SelectionCapacity)
            {
                if (!loggedSelectionCapacity)
                {
                    loggedSelectionCapacity = true;
                    Debug.LogWarning("[PickupFieldHost] motor candidate capacity " + slotCount
                        + " exceeds this host's selection buffer " + SelectionCapacity
                        + "; only the nearest " + SelectionCapacity + " are offered per scan.");
                }
                slotCount = SelectionCapacity;
            }
            if (slotCount <= 0) return;

            // The ball side of the size test: the same reading the motor's gate and
            // UpdatePropObstacles use, so the three agree on what "too big" means.
            float ballDiameterM = motor.GetSemanticDiameterWorldM();

            // The rows near the ball come from the spatial index (grid cells for static
            // props, a short list for moving ones); the verdict per row is the same
            // PickupCandidateSelection decision as before, over cached requirements.
            EnsureNearRows(center, activationRadiusM, -1f);
            bool profile = performanceDiagnostics != null && performanceDiagnostics.sampling;
            long phaseStart = profile ? BallPerformanceMath.Timestamp() : 0L;
            float ballMm;
            bool ballMmValid = PickupAbsorbMath.TryFloorMillimetres(ballDiameterM, out ballMm);

            int selected = 0;
            int inRange = 0;
            int withinLimit = 0;
            for (int k = 0; k < nearCount; k++)
            {
                int i = nearRows[k];
                if (propStates[i] != StateFree) continue;
                if (!submittable[i]) continue;

                int selectedAfter;
                int offer = PickupCandidateSelection.OfferMeasured(i, nearDistSq[k],
                    activationRadiusM,
                    PickupAbsorbMath.IsBeyondCachedRequirement(propReqMm[i], ballMmValid, ballMm),
                    nearestIndices, nearestDistSq, selected, slotCount, out selectedAfter);
                selected = selectedAfter;
                if (offer == PickupCandidateSelection.OfferOutOfRange) continue;
                inRange = inRange + 1;
                if (offer == PickupCandidateSelection.OfferWithinLimit)
                    withinLimit = withinLimit + 1;
            }

            if (profile) phaseStart = BallPerformanceMath.Split(performanceDiagnostics, BallPerformanceMath.PickupSelect, phaseStart);
            WithdrawUnselected(selected);

            for (int i = 0; i < selected; i++)
            {
                if (motor.GetPendingCandidateCount() >= slotCount) break;
                SubmitProp(nearestIndices[i]);
            }

            if (profile)
            {
                BallPerformanceMath.Split(performanceDiagnostics, BallPerformanceMath.PickupSubmit, phaseStart);
                performanceDiagnostics._Sample(BallPerformanceMath.ScanCandidates, nearCount);
                performanceDiagnostics._Sample(BallPerformanceMath.SelectedCandidates, selected);
            }
            lastScanInRange = inRange;
            lastScanWithinLimit = withinLimit;
            lastScanSelected = selected;
            lastScanQueued = queuedCount;
            LogScanOnce(now, ballDiameterM);
        }

        /// <summary>
        /// Withdraws every queued prop of this host that the scan did not select, keeping
        /// the ones the gate has accepted and the commit has not taken yet.
        /// </summary>
        /// <remarks>
        /// A row leaves the list when it was withdrawn, when the motor no longer holds its
        /// id (committed, or a motor-side clear), or when its state is no longer free. A
        /// row the motor refused to withdraw because it is accepted stays listed and is
        /// looked at again next scan.
        /// </remarks>
        private void WithdrawUnselected(int selected)
        {
            int write = 0;
            for (int read = 0; read < queuedCount; read++)
            {
                int index = queuedIndices[read];
                bool keep = false;
                if (propStates[index] == StateFree)
                {
                    if (PickupCandidateSelection.IsSelected(index, nearestIndices, selected))
                    {
                        keep = true;
                        RefreshQueuedPose(index);
                    }
                    else if (!motor.WithdrawCandidateUnlessAccepted(itemIds[index])
                        && motor.IsCandidateQueued(itemIds[index]))
                    {
                        keep = true;
                    }
                }
                if (!keep) continue;
                queuedIndices[write] = index;
                write = write + 1;
            }
            queuedCount = write;
        }

        // At most the four selected queued rows per scan. Use the same 1 mm physical drift
        // tolerance as the spatial cache; sub-millimetre jitter must not cause cross calls.
        // The pose-only API preserves acceptance, queue age and the contact window.
        private void RefreshQueuedPose(int index)
        {
            Transform prop = propTransforms[index];
            if (prop == null) return;
            Vector3 position = prop.position;
            Quaternion rotation = prop.rotation;
            if (!PickupCandidateSelection.HasPoseMoved(submittedPositions[index],
                submittedRotations[index], position, rotation, propOwnSizes[index],
                DriftToleranceM)) return;
            if (!motor.RefreshCandidatePoseUnlessAccepted(itemIds[index], position, rotation)) return;
            submittedPositions[index] = position;
            submittedRotations[index] = rotation;
        }

        /// <summary>
        /// Queues one prop. The motor de-duplicates by item id, so a repeat is a no-op that
        /// consumes no slot; that is why the slot count is re-read rather than assumed.
        /// </summary>
        private void SubmitProp(int index)
        {
            Transform prop = propTransforms[index];
            // Per-item quantities travel together: the prop's own size and k for the gate (the
            // original's Req = floor(1000*cbrt(60*k*a*b*c/pi)), a, b, c being the prop's own
            // edges, never its turned world box), the same size placed at the prop's position
            // with its world rotation for the contact test (the prop's box touching the ball),
            // and the finished growth in mm^3 for the ledger. None is derived here; the host
            // resolved the size once in Start.
            bool queued = motor.SubmitCandidateWithVolumeKMm3(true, kinds[index],
                propOwnSizes[index], prop.rotation, prop.position, growthMm3[index],
                itemIds[index], 0, propGateK[index]);
            // Listed even when the motor answered "already queued": both cases leave this
            // host responsible for withdrawing the id once it is no longer selected.
            if (queued)
            {
                // A duplicate submit did not write a pose. Keep the old baseline so small
                // moves accumulate until they exceed the physical refresh tolerance.
                for (int q = 0; q < queuedCount; q++)
                {
                    if (queuedIndices[q] == index) return;
                }
                submittedPositions[index] = prop.position;
                submittedRotations[index] = prop.rotation;
                RememberQueued(index);
            }
        }

        private void RememberQueued(int index)
        {
            for (int i = 0; i < queuedCount; i++)
            {
                if (queuedIndices[i] == index) return;
            }
            if (queuedCount >= QueuedCapacity) return;
            queuedIndices[queuedCount] = index;
            queuedCount = queuedCount + 1;
        }

        private void ForgetQueued(int index)
        {
            int write = 0;
            for (int read = 0; read < queuedCount; read++)
            {
                if (queuedIndices[read] == index) continue;
                queuedIndices[write] = queuedIndices[read];
                write = write + 1;
            }
            queuedCount = write;
        }

        /// <summary>
        /// Releases every candidate this host queued, accepted or not: a stopped round or a
        /// field reset means nothing may absorb any more.
        /// </summary>
        private void WithdrawSubmitted()
        {
            if (motor == null) return;
            for (int i = 0; i < queuedCount; i++)
            {
                motor.WithdrawCandidate(itemIds[queuedIndices[i]]);
            }
            queuedCount = 0;
        }

        /// <summary>
        /// One console line per scan-count change, at most every
        /// <see cref="ScanLogIntervalSeconds"/>: the counts an operator needs to see
        /// whether the queue holds what the ball can take.
        /// </summary>
        private void LogScanOnce(float now, float ballDiameterM)
        {
            if (now < nextScanLogTime) return;
            if (lastScanInRange == loggedScanInRange
                && lastScanWithinLimit == loggedScanWithinLimit
                && lastScanQueued == loggedScanQueued) return;
            nextScanLogTime = now + ScanLogIntervalSeconds;
            loggedScanInRange = lastScanInRange;
            loggedScanWithinLimit = lastScanWithinLimit;
            loggedScanQueued = lastScanQueued;
            Debug.Log("[PickupFieldHost] scan inRange=" + lastScanInRange
                + " withinLimit=" + lastScanWithinLimit
                + " selected=" + lastScanSelected
                + " queued=" + lastScanQueued
                + " ballMm=" + motor.GetAbsorbGateDiameterMm());
        }

        /// <summary>Props inside the activation radius at the last scan (any size).</summary>
        public int GetScanInRangeCount()
        {
            return lastScanInRange;
        }

        /// <summary>Of those, the props that passed the size gate at the ball's diameter.</summary>
        public int GetScanWithinLimitCount()
        {
            return lastScanWithinLimit;
        }

        /// <summary>Rows chosen for the queue by the last scan (nearest of the within-limit props).</summary>
        public int GetScanSelectedCount()
        {
            return lastScanSelected;
        }

        /// <summary>Candidates this host has in the motor's queue after the last scan.</summary>
        public int GetQueuedCount()
        {
            return queuedCount;
        }

        // ------------------------------------------------------------------
        // Prop obstacles: nearby absorb-proof props are walls or supports (P2).
        // ------------------------------------------------------------------

        /// <summary>
        /// Rebuilds the motor's prop solids from the nearby props the ball cannot absorb
        /// yet, once per scan interval.
        /// </summary>
        /// <remarks>
        /// <para>
        /// One scan, one classification. Per prop: one transform read, one squared centre
        /// distance, one volume comparison; for the handful inside the pruning bound, one
        /// box and one <see cref="SupportSurfaceMath.TryClassifyProp"/>. The classifier
        /// gives the prop exactly one role, so a prop can never be both a wall and a floor;
        /// <see cref="RefreshPropObbs"/> then selects the nearest solids.
        /// </para>
        /// <para>
        /// The predicate is the absorb gate's, not a copy of it: the ball side comes from
        /// the motor (<c>GetSemanticDiameterWorldM</c>) and each prop carries its own
        /// <c>propVolumeK</c>, and the test is
        /// <see cref="PickupAbsorbMath.IsBeyondAbsorbLimit"/> - the same whole-millimetre
        /// comparison <see cref="PickupAbsorbMath.EvaluateAbsorb"/> makes. The
        /// classification then splits only on <c>rise = topY - supportY</c> against the
        /// motor's <c>GetStepHeightLimitWorldM()</c>, so props and scene geometry cannot
        /// disagree about what a step is.
        /// </para>
        /// <para>
        /// The predicate reads the prop's own size, so turning a prop never changes whether
        /// it blocks. Boxes come from the gameplay volume <c>propBounds</c> around the
        /// prop's current world position - never from a collider and never from the visual
        /// mesh.
        /// </para>
        /// <para>
        /// Failures keep the registered set instead of clearing it: an empty set is exactly
        /// the "ball rolls through the world" bug this method exists to remove. The set is
        /// cleared only where it cannot be right any more - prop obstacles disabled, the
        /// motor refusing to price its own limit/step, or a field reset.
        /// </para>
        /// </remarks>
        private void UpdatePropObstacles()
        {
            if (blockingDisabled)
            {
                ClearPropSolids();
                return;
            }

            float now = Time.time;
            if (now < nextBlockingScanTime) return;
            nextBlockingScanTime = now + scanIntervalSeconds;

            float ballDiameterM = motor.GetSemanticDiameterWorldM();
            float ballMm;
            if (!KatamariMath.IsFiniteFloat(ballDiameterM) || ballDiameterM < 0f ||
                !PickupAbsorbMath.TryFloorMillimetres(ballDiameterM, out ballMm))
            {
                // The motor could not price its own diameter. Registering nothing matches
                // what the gate does with a non-finite diameter (it refuses every item), so
                // the two answers stay identical; the motor already reports the refused
                // derive.
                ClearPropSolids();
                return;
            }

            Vector3 center = motor.GetCenterWorld();
            if (!KatamariMath.IsFiniteVector(center)) return;
            float collisionRadius = motor.GetCollisionRadiusWorldM();
            float radius = collisionRadius + blockingMarginM;
            if (!KatamariMath.IsFiniteFloat(radius) || radius <= 0f) return;
            // Both numbers are SURFACE-level diagnostics (not the proxy-lifted ball bottom),
            // so a prop is classified against the same height the motor uses for a static box.
            float supportHeight = motor.GetSupportSurfaceHeightWorldM();
            float stepLimit = motor.GetStepHeightLimitWorldM();
            if (!KatamariMath.IsFiniteFloat(supportHeight) ||
                !KatamariMath.IsFiniteFloat(stepLimit) || stepLimit < 0f)
            {
                // Without a priced step there is no geometric rule to apply; keep whatever
                // was registered rather than guessing a role.
                return;
            }
            float ballBottom = center.y - collisionRadius;
            if (!KatamariMath.IsFiniteFloat(ballBottom)) return;

            // Wider than the selection radius, because SelectNearest keeps a previous
            // entry out to radius + stickiness. Pruning must never see less than a
            // selection can keep, or stickiness would silently degrade.
            float pruneRadius = radius + blockingStickinessM;
            if (!KatamariMath.IsFiniteFloat(pruneRadius)) return;

            // Last scan's candidates leave first; only the rows in candRows ever carry a flag.
            for (int k = 0; k < candCount; k++)
            {
                int previousRow = candRows[k];
                propObbCandidate[previousRow] = false;
                supportCandidate[previousRow] = false;
            }
            candCount = 0;

            // One cross-behaviour read per scan; the per-row query only runs while something falls.
            bool anyFalling = fallHost != null && fallHost.GetFallingCount() > 0;

            // The rows near the ball for this scan (and for the candidate scan, when it is due
            // in the same frame: one gather serves both). Every row the original full walk
            // could have kept is in the list; the per-row tests below are the original ones.
            float scanReach = -1f;
            if (now >= nextScanTime && Collecting()) scanReach = activationRadiusM;
            EnsureNearRows(center, scanReach, pruneRadius);

            for (int k = 0; k < nearCount; k++)
            {
                int i = nearRows[k];
                if (areaCollisionOffRows != null && areaCollisionOffRows[i]) continue;
                // Absorbed and hidden props are no longer walls or floors. A row with no
                // transform or no usable volume has no box to build, so it is skipped too
                // (not because it is unsubmittable: a prop the motor would refuse as a
                // candidate is still a solid object, and PreparePropRows keeps it).
                if (propStates[i] != StateFree) continue;
                if (!propUsable[i]) continue;
                // A falling prop is neither a wall nor a floor (PROP_BEHAVIOURS section 4).
                if (anyFalling && fallHost.IsPropFalling(i)) continue;

                // Pruning only, never a decision: the distance to an axis-aligned box is at
                // least the distance to its centre minus its half diagonal, so a prop past
                // this bound cannot be selected at radius + stickiness.
                float pruneLimit = pruneRadius + propHalfDiagonal[i];
                if (nearDistSq[k] > pruneLimit * pruneLimit) continue;

                if (!PickupAbsorbMath.IsBeyondCachedRequirement(propReqMm[i], true, ballMm))
                    continue;

                // A static row's box was built once in Start from the same position and bounds;
                // a moving row's is rebuilt from the position read this scan.
                Vector3 minimum;
                Vector3 maximum;
                if (rowMobile[i])
                {
                    if (!BlockingPropMath.TryBoxFromCentreAndExtents(nearPositions[k],
                        propBounds[i], out minimum, out maximum)) continue;
                    propMinima[i] = minimum;
                    propMaxima[i] = maximum;
                }
                else
                {
                    if (!propBoxValid[i]) continue;
                    maximum = propMaxima[i];
                }

                // Already above the top is the static box's rule 2: a ball descending onto
                // this prop must not see its landing surface classified as a wall.
                bool alreadyAbove = ballBottom >= maximum.y;
                int role;
                if (!SupportSurfaceMath.TryClassifyProp(true, maximum.y, supportHeight,
                    stepLimit, alreadyAbove, out role)) continue;
                if (role == SupportSurfaceMath.PropRoleSupport)
                {
                    supportCandidate[i] = true;
                    propObbCandidate[i] = true;
                    candRows[candCount] = i;
                    candCount = candCount + 1;
                }
                else if (role == SupportSurfaceMath.PropRoleBlocking)
                {
                    propObbCandidate[i] = true;
                    candRows[candCount] = i;
                    candCount = candCount + 1;
                }
            }

            // One predicate decides which props are walls and which are floors.
            RefreshPropObbs(center, radius);
        }

        // ------------------------------------------------------------------
        // Contact v2 prop solids (CONTACT_MODEL_V2 §4A.4/§5.2, S4b).
        // ------------------------------------------------------------------

        /// <summary>
        /// Rebuilds the v2 prop solids from this scan's candidates: at most
        /// <see cref="PropObbBakeMath.PropObbCapacity"/> true OBBs, nearest first.
        /// </summary>
        /// <remarks>
        /// <para>
        /// One predicate, one set. The candidate flags are the ones
        /// <see cref="UpdatePropObstacles"/> already computed with the absorb gate's own
        /// predicate (<see cref="PickupAbsorbMath.IsBeyondAbsorbLimit"/>) and the
        /// motor's step limit. The selection is <see cref="BlockingPropMath.SelectNearest"/>
        /// (nearest first plus stickiness) over the merged candidates, with a 12-slot
        /// capacity (6 blocking + 6 support, one table, because a prop is exactly one of
        /// the two per scan).
        /// </para>
        /// <para>
        /// The box is the prop's own: <c>propLocalBounds</c> rotated once by the prop's world
        /// rotation (<see cref="PropObbBakeMath.TryBakeProp"/>), not the world AABB rotated
        /// again. A row without a usable local column falls back to the world AABB as an
        /// axis-aligned box and is counted so the fallback is visible instead of silent.
        /// </para>
        /// </remarks>
        private void RefreshPropObbs(Vector3 center, float radiusM)
        {
            if (motor == null) return;
            if (propObbCandidate == null) return;
            // The block's capacity is the motor's; the host's buffer is sized for the same
            // 12 slots, and a motor that ever offers fewer is honoured instead of being sent
            // a batch it can only refuse.
            int capacity = motor.GetPropObbsCapacity();
            if (capacity > PropObbBakeMath.PropObbCapacity) capacity = PropObbBakeMath.PropObbCapacity;
            if (capacity <= 0) { ClearPropObbs(); ClearPropProxies(); return; }

            // The candidates are the rows UpdatePropObstacles listed this scan.
            int candidates = candCount;
            propObbCandidateCount = candidates;
            // More candidates than the block holds is the §5.4 failure mode: count it once per
            // scan so it is visible, and let the nearest-first selection keep the nearest.
            if (candidates > capacity) propObbOverflowCount = propObbOverflowCount + 1;

            int written = BlockingPropMath.SelectNearestAmong(center, radiusM, propMinima,
                propMaxima, propObbCandidate, candRows, candCount, propCount, candDistanceByRow,
                propObbPreviousIndices, propObbPreviousCount, blockingStickinessM,
                propObbSelectedIndices, propObbSelectedDistances, capacity);
            if (written < 0)
            {
                // A refused selection is a data error, not an empty world: keep the solids
                // that are registered and say nothing per scan.
                return;
            }

            if (motor.IsPropProxyContactEnabled())
            {
                proxyCandidateCount = candidates;
                int proxyBaked = BakeProxyRows(written);
                ApplyPropProxySet(proxyBaked);
                // A switch from the OBB path must not leave an old prop block behind,
                // even when this scan selected no prop (the proxy block is then empty and
                // the old OBB block would otherwise come back as the contact authority).
                if (propObbAppliedCount > 0)
                {
                    motor.ClearPropObbs();
                    propObbAppliedCount = 0;
                }
                int[] proxySwap = propObbPreviousIndices;
                propObbPreviousIndices = propObbSelectedIndices;
                propObbSelectedIndices = proxySwap;
                propObbPreviousCount = written;
                return;
            }
            if (proxyAppliedCount > 0) ClearPropProxies();

            int baked = 0;
            int fallenBack = 0;
            for (int i = 0; i < written; i++)
            {
                int index = propObbSelectedIndices[i];
                if (index < 0 || index >= propCount) continue;
                Transform prop = propTransforms[index];
                if (prop == null) continue;
                bool hasLocalRow = propLocalBounds != null && index < propLocalBounds.Length;
                Vector3 localRow = hasLocalRow ? propLocalBounds[index] : Vector3.zero;
                Vector3 axisX;
                Vector3 axisY;
                Vector3 axisZ;
                Vector3 halfExtents;
                int source = PropObbBakeMath.TryBakeProp(prop.position, prop.rotation, hasLocalRow,
                    localRow, propBounds[index], out axisX, out axisY, out axisZ, out halfExtents);
                if (source == PropObbBakeMath.SourceNone) continue;
                if (source == PropObbBakeMath.SourceWorldAabb) fallenBack = fallenBack + 1;
                propObbCenter[baked] = prop.position;
                propObbAxisX[baked] = axisX;
                propObbAxisY[baked] = axisY;
                propObbAxisZ[baked] = axisZ;
                propObbHalfExtents[baked] = halfExtents;
                // The motor's prop block convention: surface id 100 + SLOT in this block
                // (100..111), role 2 = a support prop / 3 = a blocking prop (ContactCodes),
                // so GetSupportKind()=3 and GetContactRole() read a prop contact as one.
                propObbSurfaceId[baked] = SupportSurfaceMath.FirstPropSurfaceId + baked;
                propObbRole[baked] = supportCandidate[index]
                    ? ContactCodes.RoleProp : ContactCodes.RoleBlocking;
                baked = baked + 1;
            }
            propObbFallbackRows = fallenBack;
            ApplyPropObbSet(baked);

            // Swap rather than copy: SelectNearest needs last scan's order for stickiness.
            int[] swap = propObbPreviousIndices;
            propObbPreviousIndices = propObbSelectedIndices;
            propObbSelectedIndices = swap;
            propObbPreviousCount = written;
        }

        /// <summary>
        /// Bakes this scan's selected props into the host's 12 proxy rows. A prop with no
        /// collider block (kind -1) uses the same local-OBB bake as today, so flag-on with
        /// no authored proxy data is the old box shape.
        /// </summary>
        private int BakeProxyRows(int written)
        {
            int baked = 0;
            int fallenBack = 0;
            for (int i = 0; i < written; i++)
            {
                int index = propObbSelectedIndices[i];
                if (index < 0 || index >= propCount) continue;
                Transform prop = propTransforms[index];
                if (prop == null) continue;
                int kind = ProxyKindAt(index);
                bool ok = false;
                Vector3 center = Vector3.zero;
                Vector3 axisX = Vector3.zero;
                Vector3 axisY = Vector3.zero;
                Vector3 axisZ = Vector3.zero;
                Vector3 halfExtents = Vector3.zero;
                float radius = 0f;
                float halfHeight = 0f;
                int axis = ProxyContactMath.AxisX;
                int hullCount = 0;
                int faceCount = 0;
                float boundingRadius = 0f;

                if (kind >= 0)
                {
                    int hullStart = ProxyIntAt(propProxyHullStarts, index, 0);
                    int hullCountRow = ProxyIntAt(propProxyHullCounts, index, 0);
                    int faceStart = ProxyIntAt(propProxyFaceStarts, index, 0);
                    int faceCountRow = ProxyIntAt(propProxyFaceCounts, index, 0);
                    ok = ProxyContactMath.TryBakeProxy(kind, prop.position, prop.rotation,
                        ProxyVectorAt(propProxyCenters, index, Vector3.zero),
                        ProxyVectorAt(propProxySizes, index, Vector3.zero),
                        ProxyFloatAt(propProxyRadii, index, 0f),
                        ProxyFloatAt(propProxyHalfHeights, index, 0f),
                        ProxyIntAt(propProxyAxes, index, ProxyContactMath.AxisX),
                        propProxyHullPoints, hullStart, hullCountRow,
                        propProxyHullFaces, faceStart, faceCountRow,
                        proxyHullPoints, baked * ProxyContactMath.MaxHullPoints,
                        ProxyContactMath.MaxHullPoints, proxyFaceNormals, proxyFaceOffsets,
                        proxyFaceA, proxyFaceB, proxyFaceC,
                        baked * ProxyContactMath.MaxHullFaces, ProxyContactMath.MaxHullFaces,
                        out center, out axisX, out axisY, out axisZ, out halfExtents,
                        out radius, out halfHeight, out axis, out hullCount, out faceCount,
                        out boundingRadius);
                    if (!ok && kind != ProxyContactMath.KindBox) fallenBack++;
                }

                if (!ok)
                {
                    bool hasLocalRow = propLocalBounds != null && index < propLocalBounds.Length;
                    Vector3 localRow = hasLocalRow ? propLocalBounds[index] : Vector3.zero;
                    int source = PropObbBakeMath.TryBakeProp(prop.position, prop.rotation,
                        hasLocalRow, localRow, propBounds[index], out axisX, out axisY,
                        out axisZ, out halfExtents);
                    if (source == PropObbBakeMath.SourceNone) continue;
                    kind = ProxyContactMath.KindBox;
                    center = prop.position;
                    radius = 0f;
                    halfHeight = 0f;
                    axis = ProxyContactMath.AxisX;
                    hullCount = 0;
                    faceCount = 0;
                    boundingRadius = ObbContactMath.BoundingRadius(halfExtents);
                }

                proxyKinds[baked] = kind;
                proxyCenter[baked] = center;
                proxyAxisX[baked] = axisX;
                proxyAxisY[baked] = axisY;
                proxyAxisZ[baked] = axisZ;
                proxyHalfExtents[baked] = halfExtents;
                proxyRadii[baked] = radius;
                proxyHalfHeights[baked] = halfHeight;
                proxyAxes[baked] = axis;
                proxyHullStarts[baked] = baked * ProxyContactMath.MaxHullPoints;
                proxyHullCounts[baked] = hullCount;
                proxyFaceStarts[baked] = baked * ProxyContactMath.MaxHullFaces;
                proxyFaceCounts[baked] = faceCount;
                proxySurfaceIds[baked] = SupportSurfaceMath.FirstPropSurfaceId + baked;
                proxyRoles[baked] = supportCandidate[index]
                    ? ContactCodes.RoleProp : ContactCodes.RoleBlocking;
                baked = baked + 1;
            }

            proxyFallbackRows = fallenBack;
            return baked;
        }

        private int ProxyKindAt(int index)
        {
            if (propProxyKinds == null || index < 0 || index >= propProxyKinds.Length)
                return -1;
            int kind = propProxyKinds[index];
            if (kind < ProxyContactMath.KindBox || kind > ProxyContactMath.KindConvex)
                return -1;
            if (propProxyCenters == null || index >= propProxyCenters.Length ||
                !KatamariMath.IsFiniteVector(propProxyCenters[index])) return -1;
            if (kind == ProxyContactMath.KindBox)
            {
                if (propProxySizes == null || index >= propProxySizes.Length) return -1;
                Vector3 size = propProxySizes[index];
                // A zero default array is not a box: it is the un-synced/absent row, and must
                // fall back to the old OBB. A real box has three positive edges.
                if (!KatamariMath.IsFiniteVector(size) || size.x <= 0f || size.y <= 0f ||
                    size.z <= 0f) return -1;
                Vector3 half = new Vector3(size.x * 0.5f, size.y * 0.5f, size.z * 0.5f);
                return ProxyContactMath.IsValidProxy(kind, propProxyCenters[index],
                    Vector3.right, Vector3.up, Vector3.forward, half, 0f, 0f, 0, 0, 0)
                    ? kind : -1;
            }
            if (kind == ProxyContactMath.KindSphere)
            {
                float radius = ProxyFloatAt(propProxyRadii, index, -1f);
                return ProxyContactMath.IsValidProxy(kind, propProxyCenters[index],
                    Vector3.right, Vector3.up, Vector3.forward, Vector3.zero, radius, 0f,
                    0, 0, 0) ? kind : -1;
            }
            if (kind == ProxyContactMath.KindCapsule)
            {
                float radius = ProxyFloatAt(propProxyRadii, index, -1f);
                float halfHeight = ProxyFloatAt(propProxyHalfHeights, index, -1f);
                int axis = ProxyIntAt(propProxyAxes, index, -1);
                return ProxyContactMath.IsValidProxy(kind, propProxyCenters[index],
                    Vector3.right, Vector3.up, Vector3.forward, Vector3.zero, radius,
                    halfHeight, axis, 0, 0) ? kind : -1;
            }

            int hullStart = ProxyIntAt(propProxyHullStarts, index, -1);
            int hullCount = ProxyIntAt(propProxyHullCounts, index, 0);
            int faceStart = ProxyIntAt(propProxyFaceStarts, index, -1);
            int faceCount = ProxyIntAt(propProxyFaceCounts, index, 0);
            if (propProxyHullPoints == null || propProxyHullFaces == null) return -1;
            if (hullStart < 0 || hullCount < 4 || hullCount > ProxyContactMath.MaxHullPoints ||
                hullStart + hullCount > propProxyHullPoints.Length) return -1;
            if (faceStart < 0 || faceCount < 1 || faceCount > ProxyContactMath.MaxHullFaces ||
                faceStart + faceCount * 3 > propProxyHullFaces.Length) return -1;
            return ProxyContactMath.IsValidProxy(kind, propProxyCenters[index], Vector3.right,
                Vector3.up, Vector3.forward, Vector3.zero,
                ProxyFloatAt(propProxyRadii, index, 0f),
                ProxyFloatAt(propProxyHalfHeights, index, 0f),
                ProxyIntAt(propProxyAxes, index, 0), hullCount, faceCount) ? kind : -1;
        }

        private static Vector3 ProxyVectorAt(Vector3[] values, int index, Vector3 fallback)
        {
            if (values == null || index < 0 || index >= values.Length) return fallback;
            return values[index];
        }

        private static float ProxyFloatAt(float[] values, int index, float fallback)
        {
            if (values == null || index < 0 || index >= values.Length) return fallback;
            return values[index];
        }

        private static int ProxyIntAt(int[] values, int index, int fallback)
        {
            if (values == null || index < 0 || index >= values.Length) return fallback;
            return values[index];
        }

        /// <summary>
        /// Hands this scan's 12 proxies to the motor, or clears the block when there is none.
        /// </summary>
        private void ApplyPropProxySet(int count)
        {
            proxyCount = count;
            if (motor == null) return;
            if (count <= 0)
            {
                if (proxyAppliedCount == 0) return;
                motor.ClearPropProxies();
                proxyAppliedCount = 0;
                return;
            }
            if (!motor.IsPropProxyContactEnabled()) return;
            if (motor.SetPropProxies(count, proxyKinds, proxyCenter, proxyAxisX, proxyAxisY,
                proxyAxisZ, proxyHalfExtents, proxyRadii, proxyHalfHeights, proxyAxes,
                proxyHullPoints, proxyHullStarts, proxyHullCounts, proxyFaceNormals,
                proxyFaceOffsets, proxyFaceA, proxyFaceB, proxyFaceC, proxyFaceStarts,
                proxyFaceCounts, proxySurfaceIds, proxyRoles))
            {
                proxyAppliedCount = count;
                return;
            }
            if (proxyLogCount >= BlockingLogLimit) return;
            proxyLogCount = proxyLogCount + 1;
            Debug.LogWarning("[PickupFieldHost] the motor refused the v2 prop proxies ("
                + motor.GetPropProxyRefusalReason() + "); keeping the previous set of "
                + motor.GetPropProxiesCount() + ".");
        }

        private void ClearPropProxies()
        {
            if (proxyAppliedCount > 0 && motor != null)
            {
                motor.ClearPropProxies();
                proxyAppliedCount = 0;
            }
            if (proxyCount == 0 && proxyFallbackRows == 0) return;
            proxyCount = 0;
            proxyCandidateCount = 0;
            proxyFallbackRows = 0;
        }

        /// <summary>
        /// Hands this scan's v2 prop solids to the motor's prop block, and clears the block
        /// when the scan has none.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <c>SetPropObbs</c> is a WHOLE-BLOCK replace of 12 slots (100..111), so the host
        /// sends exactly what this scan selected and never has to track removals itself: a
        /// prop that was absorbed, moved out of range or stopped being absorb-proof simply
        /// is not in the next batch.
        /// </para>
        /// <para>
        /// A refused call keeps the motor's previous block (its contract) and is counted by
        /// the motor; this host logs the reason once per category, never per scan.
        /// </para>
        /// </remarks>
        private void ApplyPropObbSet(int count)
        {
            propObbCount = count;
            if (motor == null) return;
            if (count <= 0)
            {
                if (propObbAppliedCount == 0) return;
                motor.ClearPropObbs();
                propObbAppliedCount = 0;
                CountPropObbRoles(0);
                return;
            }
            if (motor.SetPropObbs(propObbCenter, propObbAxisX, propObbAxisY, propObbAxisZ,
                propObbHalfExtents, propObbSurfaceId, propObbRole, count))
            {
                propObbAppliedCount = count;
                CountPropObbRoles(count);
                return;
            }
            if (propObbLogCount >= BlockingLogLimit) return;
            propObbLogCount = propObbLogCount + 1;
            Debug.LogWarning("[PickupFieldHost] the motor refused the v2 prop solids ("
                + motor.GetPropObbsRefusalReason() + "); keeping the previous set of "
                + motor.GetPropObbsCount() + ".");
        }

        /// <summary>Forgets the v2 prop solids and empties the motor's prop block.</summary>
        private void ClearPropObbs()
        {
            if (propObbAppliedCount > 0 && motor != null)
            {
                motor.ClearPropObbs();
                propObbAppliedCount = 0;
                CountPropObbRoles(0);
            }
            if (propObbCount == 0 && propObbPreviousCount == 0) return;
            propObbCount = 0;
            propObbPreviousCount = 0;
            propObbCandidateCount = 0;
            propObbFallbackRows = 0;
        }

        // Drops the motor's prop solids and this host's memory of them. Used where the set
        // cannot be right any more, never as a failure response.
        private void ClearPropSolids()
        {
            ClearPropObbs();
            ClearPropProxies();
        }

        private void CountPropObbRoles(int count)
        {
            int blocking = 0;
            int support = 0;
            for (int i = 0; i < count; i++)
            {
                if (propObbRole[i] == ContactCodes.RoleProp) support = support + 1;
                else blocking = blocking + 1;
            }
            propObbBlockingCount = blocking;
            propObbSupportCount = support;
        }

        /// <summary>
        /// Blocking prop solids registered with the motor by the last scan (0..12).
        /// </summary>
        public int GetBlockingPropCount()
        {
            return propObbBlockingCount;
        }

        /// <summary>Whether this field registers prop obstacles (walls and supports) at all.</summary>
        public bool IsBlockingEnabled()
        {
            return !blockingDisabled;
        }

        /// <summary>
        /// v2 prop solids this host built for the last scan (0..12). Populated only while the
        /// motor reports contact v2; they are NOT registered yet - see
        /// <see cref="ApplyPropObbSet"/> for the pending motor API.
        /// </summary>
        public int GetPropObbCount()
        {
            return propObbCount;
        }

        /// <summary>Props that were blocking or support candidates in the last scan (v2 view).</summary>
        public int GetPropObbCandidateCount()
        {
            return propObbCandidateCount;
        }

        /// <summary>Built solids that had to use the world AABB instead of the local column.</summary>
        public int GetPropObbFallbackRows()
        {
            return propObbFallbackRows;
        }

        /// <summary>Scans whose candidate set exceeded the 12-slot v2 table (visible, not dropped silently).</summary>
        public int GetPropObbOverflowCount()
        {
            return propObbOverflowCount;
        }

        /// <summary>Support prop solids registered with the motor by the last scan (0..12).</summary>
        public int GetSupportPropCount()
        {
            return propObbSupportCount;
        }


        // ------------------------------------------------------------------
        // One-shot diagnostics.
        // ------------------------------------------------------------------

        private void LogMotorMissingOnce()
        {
            if (loggedMotorMissing) return;
            loggedMotorMissing = true;
            Debug.LogWarning("[PickupFieldHost] motor reference is null; this field does nothing.");
        }

        private void LogRootMissingOnce()
        {
            if (loggedRootMissing) return;
            loggedRootMissing = true;
            Debug.LogWarning("[PickupFieldHost] attachmentRoot is null; not submitting candidates"
                + " (absorbed props could only be hidden). Wire a root under BallVisualRollRoot"
                + " with identity local position and scale 1.");
        }
    }
}
