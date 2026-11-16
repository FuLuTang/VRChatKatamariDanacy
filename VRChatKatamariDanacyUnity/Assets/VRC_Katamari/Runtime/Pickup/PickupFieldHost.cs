using UdonSharp;
using UnityEngine;
using VRCKatamari.BallContact;
using VRCKatamari.BallLayers;
using VRCKatamari.BallMotor;
using VRCKatamari.Core;

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
    /// The pose comes from the prop's CURRENT transform, never from the event snapshot.
    /// The motor records the position the candidate had when it was queued
    /// (<c>pendingPositions</c>), so at high speed that snapshot can be tens of centimetres
    /// behind where the prop actually is. The event is only used for its item id.
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
    /// Attach or hide, never both. A prop whose pose the math refuses is hidden
    /// (<c>SetActive(false)</c>) and logged once with a reason code, because a prop that
    /// was absorbed but cannot be attached must not keep floating in the world. The two
    /// counts are separate: only attached props count toward the per-kind tally the story
    /// level's category goal will read.
    /// </para>
    /// <para>
    /// Prop obstacles (P2). A prop the ball cannot absorb yet must stop the ball instead of
    /// letting it roll through, and the ball's only collision authority is the motor's
    /// sweep, so this host registers the nearby absorb-proof props as the motor's dynamic
    /// blocking boxes. A prop whose top is within the motor's step limit is instead a step
    /// and a floor, and is registered through <c>SetSupportProps</c>; both roles come from
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
    /// (Docs/Design/PickupAbsorb/BLOCKING_PROPS_PLAN.md). Absorbed props are never
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

        // The motor's blocking capacity is six boxes and its support capacity is six
        // tops. This buffer is deliberately larger, same reason as SelectionCapacity: a
        // bigger motor capacity then needs no change here, and an unexpected one is
        // clamped with a single log.
        private const int BlockingSelectionCapacity = 8;

        // How far past the ball's collision radius a blocking or support prop may be and
        // still be registered. It covers one scan interval of travel with slack - 4 m/s *
        // 0.05 s = 0.2 m, so 0.8 m leaves room for a slower scan or a higher speed - and
        // is not a gameplay threshold: a prop closer than this is a wall/step, a prop
        // farther is simply not registered yet (BLOCKING_PROPS_PLAN.md §2 A). The support
        // set reuses this one number on purpose: the design says the two margins are the
        // same, and a second serialized knob could only drift.
        private const float DefaultBlockingMarginM = 0.8f;

        // A prop already in the set stays until it is clearly out of the margin, so a prop
        // hovering at the edge does not add and remove its box every scan. Purely a
        // hysteresis on the selection, not a second "is it blocking" rule.
        private const float DefaultBlockingStickinessM = 0.25f;

        // Support uses its own, smaller hysteresis: keeping a surface the ball has already
        // walked off reads worse than keeping a wall (design §6.3).
        private const float DefaultSupportStickinessM = 0.10f;

        // Same ceiling pattern as HiddenLogLimit: one line per failure category, ever.
        private const int BlockingLogLimit = 3;

        [Header("Ball")]
        [SerializeField] private KatamariBallMotor motor;

        // The scene places this under BallVisualRollRoot: identity local position, scale 1.
        // The host never moves or scales it.
        [SerializeField] private Transform attachmentRoot;

        [Header("Props: one entry per prop, index-aligned")]
        [SerializeField] private Transform[] propTransforms;
        [SerializeField] private string[] itemIds;
        [SerializeField] private string[] kinds;
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
        // Optional: wired colliders are disabled on attach so an absorbed prop cannot block
        // anything. Leave the array empty to skip that.
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
        [SerializeField] private float supportStickinessM = DefaultSupportStickinessM;

        // ---- Per-prop store (allocated in Start, never serialized) ----
        private int[] propStates;
        // The rows this host has queued in the motor (see QueuedCapacity): the withdraw list.
        private int[] queuedIndices;
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
        // Per-scan candidate flags, written for every prop so a stale true cannot survive.
        // The classifier sets at most one of them per prop.
        private bool[] blockingCandidate;
        private bool[] supportCandidate;
        // Selection scratch: SelectNearest reads the previous batch while writing the
        // current one, so the two index arrays are swapped rather than copied.
        private int[] blockingPreviousIndices;
        private int[] blockingSelectedIndices;
        private float[] blockingSelectedDistances;
        private Vector3[] blockingOutputMinima;
        private Vector3[] blockingOutputMaxima;
        private int previousBlockingCount;
        private int blockingAppliedCount;
        private int blockingCandidateCount;
        private int blockingCapacity;
        private int propUsableCount;
        private float nextBlockingScanTime;
        private int blockingLogCount;

        // Support selection has its own previous batch, capacity reading and output rows;
        // the two pools must not share stickiness or slots.
        private int[] supportPreviousIndices;
        private int[] supportSelectedIndices;
        private float[] supportSelectedDistances;
        private Vector3[] supportOutputMinima;
        private Vector3[] supportOutputMaxima;
        private int previousSupportCount;
        private int supportAppliedCount;
        private int supportCandidateCount;
        private int supportCapacity;
        private int supportLogCount;
        private bool loggedSupportSet;

        // ---- Contact v2 prop solids (CONTACT_MODEL_V2 §4A.4/§5.2, stage S4b) ----
        // One TRUE OBB per selected prop for the v2 contact solver. Built from
        // propLocalBounds + the prop's world rotation, never from the double-rotated world
        // AABB (RadialProxyLocalBoundsTests pins that bug). The set is refreshed on the same
        // scan as the v1 blocking/support sets and from the SAME candidate flags, so a prop
        // is a wall or a floor in both models and never two things at once. Unlike the v1
        // sets it has its own stickiness memory and its own 12-slot capacity (6 + 6).
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
        private bool loggedBlockingSet;
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
            ReportPropDataProblems();
            seenEvents = 0;
            nextScanTime = 0f;
            nextBlockingScanTime = 0f;
            Debug.Log("[PickupFieldHost] ready props=" + propCount
                + " submittable=" + submittableCount
                + " propRows=" + propUsableCount
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
            if (motor == null)
            {
                LogMotorMissingOnce();
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

            ConsumeAbsorbedEvents();
            UpdatePropObstacles();
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
            if (propStates == null || propTransforms == null) return false;

            // Withdraw first: the attached/hidden rows no longer hold a submit time, so
            // this only covers rows that were queued and left free.
            WithdrawSubmitted();

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
            // The withdraw list goes too: the caller has already had the motor clear its
            // candidate queue, so nothing this host listed is queued any more and the next
            // scan starts from an empty list.
            queuedCount = 0;

            // Both prop sets are a function of where the ball is, so nothing about them
            // survives a reset; static planes and boxes stay in the motor (the map did not
            // move). Clearing here as well as in TeleportTo covers a reset that does not
            // teleport, and the next scan rebuilds from the restored field.
            if (motor != null) motor.ClearBlockingBoxes();
            if (motor != null) motor.ClearSupportProps();
            // Every registered proxy item is one of the props just restored, so the shell
            // is emptied rather than edited. Clearing here as well as in the motor's own
            // round reset covers a field reset that does not restart the ball.
            if (motor != null) motor.ClearRadialProxies();
            blockingAppliedCount = 0;
            blockingCandidateCount = 0;
            previousBlockingCount = 0;
            supportAppliedCount = 0;
            supportCandidateCount = 0;
            previousSupportCount = 0;
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

        /// <summary>Rows in the optional <c>propLocalBounds</c> column (0 when absent).</summary>
        public int GetPropLocalBoundsRowCount()
        {
            if (propLocalBounds == null) return 0;
            return propLocalBounds.Length;
        }

        /// <summary>Rows in the optional collider-proxy kind column (0 when absent).</summary>
        public int GetPropProxyRowCount()
        {
            if (propProxyKinds == null) return 0;
            return propProxyKinds.Length;
        }

        /// <summary>The authored proxy kind for one row (-1 = no block / OBB fallback).</summary>
        public int GetPropProxyKind(int index)
        {
            return ProxyKindAt(index);
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
            if (!KatamariMath.IsFiniteFloat(supportStickinessM) || supportStickinessM < 0f)
                supportStickinessM = DefaultSupportStickinessM;
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

        private int LengthOfGrowth()
        {
            if (propGrowthMm3 == null) return 0;
            return propGrowthMm3.Length;
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
            blockingCandidate = new bool[propCount];
            blockingPreviousIndices = new int[BlockingSelectionCapacity];
            blockingSelectedIndices = new int[BlockingSelectionCapacity];
            blockingSelectedDistances = new float[BlockingSelectionCapacity];
            blockingOutputMinima = new Vector3[BlockingSelectionCapacity];
            blockingOutputMaxima = new Vector3[BlockingSelectionCapacity];
            previousBlockingCount = 0;
            blockingAppliedCount = 0;
            blockingCandidateCount = 0;
            blockingCapacity = 0;
            propUsableCount = 0;
            blockingLogCount = 0;
            supportCandidate = new bool[propCount];
            supportPreviousIndices = new int[BlockingSelectionCapacity];
            supportSelectedIndices = new int[BlockingSelectionCapacity];
            supportSelectedDistances = new float[BlockingSelectionCapacity];
            supportOutputMinima = new Vector3[BlockingSelectionCapacity];
            supportOutputMaxima = new Vector3[BlockingSelectionCapacity];
            previousSupportCount = 0;
            supportAppliedCount = 0;
            supportCandidateCount = 0;
            supportCapacity = 0;
            supportLogCount = 0;
            loggedSupportSet = false;
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
            if (propColliders == null || propColliders.Length < propCount)
            {
                if (!loggedNoColliders)
                {
                    loggedNoColliders = true;
                    Debug.LogWarning("[PickupFieldHost] prop colliders not fully wired;"
                        + " attached props stay collidable. props=" + propCount);
                }
            }
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
                AttachProp(index);
            }
            seenEvents = total;
        }

        /// <summary>Index of the prop with this item id, or -1 when it is not this host's.</summary>
        private int FindPropIndex(string itemId)
        {
            for (int i = 0; i < propCount; i++)
            {
                if (itemIds[i] == itemId) return i;
            }
            return -1;
        }

        // ------------------------------------------------------------------
        // Attach: ball-frame local pose, then parent under the attachment root.
        // ------------------------------------------------------------------

        private void AttachProp(int index)
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

            Vector3 worldPosition = prop.position;
            Quaternion worldRotation = prop.rotation;
            Vector3 worldScale = prop.lossyScale;
            if (!IsUsableScale(worldScale))
            {
                HideProp(index, HiddenReasonScaleInvalid);
                return;
            }

            // The ball's rigid frame, as the motor reports it: centre, and the visual roll
            // rotation that actually carries the props around.
            Vector3 ballCenter = motor.GetCenterWorld();
            Vector3 localPosition;
            Quaternion localRotation;
            if (!AttachmentMath.TryWorldPoseToLocal(ballCenter,
                motor.GetVisualRollRotation(), worldPosition, worldRotation, worldScale,
                out localPosition, out localRotation))
            {
                HideProp(index, HiddenReasonPoseRefused);
                return;
            }

            prop.SetParent(attachmentRoot);
            prop.localPosition = localPosition;
            prop.localRotation = localRotation;
            if (propColliders != null && index < propColliders.Length && propColliders[index] != null)
                propColliders[index].enabled = false;

            // The shell wants the prop's size along ITS OWN local axes, in the same
            // ball-local pose the visual prop just took. localRotation already contains the
            // prop's own rotation, so passing the world AABB (propBounds) would turn a yawed
            // prop twice. The motor decides whether this item is a long object worth an
            // endpoint candidate; a small or buried prop is skipped there, and this host
            // owns no proxy state.
            motor.AttachRadialProxy(itemIds[index], propOwnSizes[index], localPosition,
                localRotation);

            propStates[index] = StateAttached;
            ForgetQueued(index);
            // The prop may have been supporting the ball or blocking it a moment ago. The
            // obstacle refresh runs later in this same Update, before the collecting gate,
            // so pulling the next scan forward makes the absorbed prop lose both roles in
            // the frame the event is consumed instead of up to one scan later.
            nextBlockingScanTime = 0f;
            attachedCount = attachedCount + 1;
            TallyKind(kinds[index]);
            CheckAttachmentRootOnce(ballCenter);
            Debug.Log("[PickupFieldHost] attached id=" + itemIds[index]
                + " kind=" + kinds[index]
                + " attached=" + attachedCount);
        }

        private bool IsUsableScale(Vector3 scale)
        {
            if (!KatamariMath.IsFiniteVector(scale)) return false;
            // AttachmentMath rejects a non-positive scale; the reason is recorded separately
            // here so a zero-scaled prefab is not reported as an invalid rotation.
            return scale.x > 0f && scale.y > 0f && scale.z > 0f;
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
            if (kind == null) return;
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

            int selected = 0;
            int inRange = 0;
            int withinLimit = 0;
            for (int i = 0; i < propCount; i++)
            {
                if (propStates[i] != StateFree) continue;
                if (!submittable[i]) continue;
                Transform prop = propTransforms[i];
                if (prop == null) continue;

                int selectedAfter;
                int offer = PickupCandidateSelection.OfferProp(i, prop.position, center,
                    activationRadiusM, propOwnSizes, propGateK, ballDiameterM,
                    nearestIndices, nearestDistSq, selected, slotCount, out selectedAfter);
                selected = selectedAfter;
                if (offer == PickupCandidateSelection.OfferOutOfRange) continue;
                inRange = inRange + 1;
                if (offer == PickupCandidateSelection.OfferWithinLimit)
                    withinLimit = withinLimit + 1;
            }

            WithdrawUnselected(selected);

            for (int i = 0; i < selected; i++)
            {
                if (motor.GetPendingCandidateCount() >= slotCount) break;
                SubmitProp(nearestIndices[i]);
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

        /// <summary>
        /// Queues one prop. The motor de-duplicates by item id, so a repeat is a no-op that
        /// consumes no slot; that is why the slot count is re-read rather than assumed.
        /// </summary>
        private void SubmitProp(int index)
        {
            Vector3 itemPosition = propTransforms[index].position;
            // Per-item quantities travel together: the prop's own size and k for the gate (the
            // original's Req = floor(1000*cbrt(60*k*a*b*c/pi)), a, b, c being the prop's own
            // edges, never its turned world box) and for the contact reach (the size's
            // circumscribed sphere), and the finished growth in mm^3 for the ledger. None is
            // derived here; the host resolved them once in Start.
            bool queued = motor.SubmitCandidateWithVolumeKMm3(true, kinds[index], propOwnSizes[index],
                itemPosition, growthMm3[index], itemIds[index], 0, propGateK[index]);
            // Listed even when the motor answered "already queued": both cases leave this
            // host responsible for withdrawing the id once it is no longer selected.
            if (queued) RememberQueued(index);
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
        /// Rebuilds the motor's blocking AND support prop sets from the nearby props the
        /// ball cannot absorb yet, once per scan interval.
        /// </summary>
        /// <remarks>
        /// <para>
        /// One scan, one classification, two selections. Per prop: one transform read, one
        /// squared centre distance, one volume comparison; for the handful inside the
        /// pruning bound, one box and one
        /// <see cref="SupportSurfaceMath.TryClassifyProp"/>. The classifier gives the prop
        /// exactly one role, so a prop can never be in both masks; the two sets are then
        /// selected independently by <see cref="BlockingPropMath.SelectNearest"/> (same
        /// function, separate previous batches, capacities and stickiness), which is what
        /// stops a crowded wall set from evicting the step the ball is climbing.
        /// </para>
        /// <para>
        /// The predicate is the absorb gate's, not a copy of it: the ball side comes from
        /// the motor (<c>GetSemanticDiameterWorldM</c>) and each prop carries its own
        /// <c>propVolumeK</c>, and the test is
        /// <see cref="PickupAbsorbMath.IsBeyondAbsorbLimit"/> - the same whole-millimetre
        /// comparison <see cref="PickupAbsorbMath.EvaluateAbsorb"/> makes. The
        /// classification then splits only on <c>rise = topY - supportY</c> against the
        /// motor's <c>GetStepHeightLimitWorldM()</c> - the same step limit the static-box
        /// mask uses, so props and scene geometry cannot disagree about what a step is.
        /// </para>
        /// <para>
        /// The predicate reads the prop's own size, so turning a prop never changes whether it
        /// blocks. Boxes come from the gameplay volume <c>propBounds</c> around the prop's
        /// current world position - never from a collider and never from the visual mesh.
        /// Rotation is not applied (an axis-aligned box around the authored volume), so a
        /// turned prop can register slightly early; that is the documented AABB choice, not
        /// an oversight.
        /// </para>
        /// <para>
        /// Failures keep the registered set instead of clearing it: an empty set is exactly
        /// the "ball rolls through the world" bug this method exists to remove. The sets are
        /// cleared only where they cannot be right any more - prop obstacles disabled, the
        /// motor refusing to price its own limit/step, or a field reset.
        /// </para>
        /// </remarks>
        private void UpdatePropObstacles()
        {
            if (blockingDisabled)
            {
                // One switch governs both roles: a field that declares its props
                // non-obstacles must not leave a support table behind either.
                ClearPropSets();
                return;
            }

            float now = Time.time;
            if (now < nextBlockingScanTime) return;
            nextBlockingScanTime = now + scanIntervalSeconds;

            int blockingCapacityNow = motor.GetBlockingBoxCapacity();
            int supportCapacityNow = motor.GetSupportPropCapacity();
            // The motor ignores support props while terrain support is off (it passes an
            // empty table). Registering nothing in that state keeps the host from carrying
            // a set nothing reads and makes the switch answer identical on both sides.
            bool supportRoleEnabled = motor.IsTerrainSupportEnabled();
            // v2 has no step mask and derives support from the contact itself (§7.5.4), so a
            // prop floor must keep its support role there even when the v1 terrain switch is
            // off. The v1 sets are still filled exactly as before (the switch only demotes
            // the role for THEM, and this host keeps them for a rollback to v1).
            bool v2Solids = motor.IsContactModelV2();
            bool propsCanBeFloors = supportRoleEnabled || v2Solids;
            if (!supportRoleEnabled)
            {
                supportCapacityNow = 0;
                if (supportAppliedCount > 0 || previousSupportCount > 0)
                {
                    motor.ClearSupportProps();
                    supportAppliedCount = 0;
                    previousSupportCount = 0;
                    supportCandidateCount = 0;
                }
            }
            blockingCapacity = blockingCapacityNow;
            supportCapacity = supportCapacityNow;
            if (blockingCapacityNow <= 0 && supportCapacityNow <= 0) return;
            if (blockingCapacityNow > BlockingSelectionCapacity)
            {
                if (blockingLogCount < BlockingLogLimit)
                {
                    LogBlockingOnce("[PickupFieldHost] motor blocking capacity " + blockingCapacityNow
                        + " exceeds this host's selection buffer " + BlockingSelectionCapacity
                        + "; only the nearest " + BlockingSelectionCapacity
                        + " are registered per scan.");
                }
                blockingCapacityNow = BlockingSelectionCapacity;
            }
            if (supportCapacityNow > BlockingSelectionCapacity)
            {
                if (supportLogCount < BlockingLogLimit)
                {
                    LogSupportOnce("[PickupFieldHost] motor support capacity " + supportCapacityNow
                        + " exceeds this host's selection buffer " + BlockingSelectionCapacity
                        + "; only the nearest " + BlockingSelectionCapacity
                        + " are registered per scan.");
                }
                supportCapacityNow = BlockingSelectionCapacity;
            }

            float ballDiameterM = motor.GetSemanticDiameterWorldM();
            float ballMm;
            if (!KatamariMath.IsFiniteFloat(ballDiameterM) || ballDiameterM < 0f ||
                !PickupAbsorbMath.TryFloorMillimetres(ballDiameterM, out ballMm))
            {
                // The motor could not price its own diameter. Registering nothing matches
                // what the gate does with a non-finite diameter (it refuses every item), so
                // the two answers stay identical; the motor already reports the refused
                // derive.
                ClearPropSets();
                return;
            }

            Vector3 center = motor.GetCenterWorld();
            if (!KatamariMath.IsFiniteVector(center)) return;
            float collisionRadius = motor.GetCollisionRadiusWorldM();
            float radius = collisionRadius + blockingMarginM;
            if (!KatamariMath.IsFiniteFloat(radius) || radius <= 0f) return;
            // The center comes from the same pose the support table was resolved at. Both
            // numbers are SURFACE-level diagnostics (not the proxy-lifted ball bottom), so
            // a prop is classified against the same height the motor uses for a static box.
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

            // Wider than either selection radius, because SelectNearest keeps a previous
            // entry out to radius + that set's stickiness. Pruning must never see less
            // than a selection can keep, or stickiness would silently degrade.
            float pruneStickiness = blockingStickinessM;
            if (supportStickinessM > pruneStickiness) pruneStickiness = supportStickinessM;
            float pruneRadius = radius + pruneStickiness;
            if (!KatamariMath.IsFiniteFloat(pruneRadius)) return;

            int blockingCandidateCountNow = 0;
            int supportCandidateCountNow = 0;
            for (int i = 0; i < propCount; i++)
            {
                blockingCandidate[i] = false;
                supportCandidate[i] = false;
                // Absorbed and hidden props are no longer walls or floors. A row with no
                // transform or no usable volume has no box to build, so it is skipped too
                // (not because it is unsubmittable: a prop the motor would refuse as a
                // candidate is still a solid object, and PreparePropRows keeps it).
                if (propStates[i] != StateFree) continue;
                if (!propUsable[i]) continue;

                Vector3 position = propTransforms[i].position;
                float dx = position.x - center.x;
                float dy = position.y - center.y;
                float dz = position.z - center.z;
                float distanceSq = dx * dx + dy * dy + dz * dz;
                if (!KatamariMath.IsFiniteFloat(distanceSq)) continue;

                // Pruning only, never a decision: the distance to an axis-aligned box is at
                // least the distance to its centre minus its half diagonal, so a prop past
                // this bound cannot be selected at radius + stickiness.
                float pruneLimit = pruneRadius + propHalfDiagonal[i];
                if (distanceSq > pruneLimit * pruneLimit) continue;

                if (!PickupAbsorbMath.IsBeyondAbsorbLimit(propOwnSizes[i], EffectiveVolumeK(i),
                    ballDiameterM)) continue;

                Vector3 minimum;
                Vector3 maximum;
                if (!BlockingPropMath.TryBoxFromCentreAndExtents(position, propBounds[i],
                    out minimum, out maximum)) continue;
                propMinima[i] = minimum;
                propMaxima[i] = maximum;

                // Already above the top is the static box's rule 2: a ball descending onto
                // this prop must not see its landing surface classified as a wall.
                bool alreadyAbove = ballBottom >= maximum.y;
                int role;
                if (!SupportSurfaceMath.TryClassifyProp(true, maximum.y, supportHeight,
                    stepLimit, alreadyAbove, out role)) continue;
                if (role == SupportSurfaceMath.PropRoleSupport)
                {
                    if (propsCanBeFloors)
                    {
                        supportCandidate[i] = true;
                        supportCandidateCountNow = supportCandidateCountNow + 1;
                    }
                    else
                    {
                        // Terrain support is off, so this prop cannot be a floor. The
                        // pre-P2 world made every absorb-proof prop a wall; keeping that
                        // fallback is what makes the switch bit-identical instead of
                        // letting the ball roll through low props.
                        blockingCandidate[i] = true;
                        blockingCandidateCountNow = blockingCandidateCountNow + 1;
                    }
                }
                else if (role == SupportSurfaceMath.PropRoleBlocking)
                {
                    blockingCandidate[i] = true;
                    blockingCandidateCountNow = blockingCandidateCountNow + 1;
                }
            }
            blockingCandidateCount = blockingCandidateCountNow;
            supportCandidateCount = supportCandidateCountNow;

            // Support first: it never rebuilds the sweep, and the ball's floor is the more
            // important fact for this step. A refused selection keeps that set's previous
            // batch and leaves the other set free to update, exactly as before P2.
            UpdateSupportSelection(center, radius, supportCapacityNow);
            UpdateBlockingSelection(center, radius, blockingCapacityNow, ballMm);
            // The v2 solids are refreshed from the same scan, the same classifier and the
            // same candidate flags: one predicate decides all four sets (v1 blocking, v1
            // support, v2 blocking, v2 support), which is the property the design asks for.
            RefreshPropObbs(center, radius);
        }

        /// <summary>Builds, validates and registers the support selection of this scan.</summary>
        private void UpdateSupportSelection(Vector3 center, float radius, int capacity)
        {
            if (capacity <= 0) return;
            int written = BlockingPropMath.SelectNearest(center, radius,
                propMinima, propMaxima, supportCandidate, propCount,
                supportPreviousIndices, previousSupportCount, supportStickinessM,
                supportSelectedIndices, supportSelectedDistances, capacity);
            if (written < 0)
            {
                LogSupportOnce("[PickupFieldHost] support selection refused its input;"
                    + " keeping the previous support set.");
                return;
            }

            for (int i = 0; i < written; i++)
            {
                int index = supportSelectedIndices[i];
                supportOutputMinima[i] = propMinima[index];
                supportOutputMaxima[i] = propMaxima[index];
            }
            // The motor reports a prop surface as 100 + slot, and SelectNearest's order is
            // distance-driven (it renumbers when the ball moves). Sorting the selected rows
            // by prop index makes a prop's surface id stable for as long as it stays
            // selected, which keeps the motor's "did the surface change" test meaningful.
            // Sorting does not change membership: the next scan keeps every previous entry
            // inside its sticky radius regardless of order.
            for (int i = 1; i < written; i++)
            {
                int index = supportSelectedIndices[i];
                Vector3 minimum = supportOutputMinima[i];
                Vector3 maximum = supportOutputMaxima[i];
                float distance = supportSelectedDistances[i];
                int j = i - 1;
                while (j >= 0 && supportSelectedIndices[j] > index)
                {
                    supportSelectedIndices[j + 1] = supportSelectedIndices[j];
                    supportOutputMinima[j + 1] = supportOutputMinima[j];
                    supportOutputMaxima[j + 1] = supportOutputMaxima[j];
                    supportSelectedDistances[j + 1] = supportSelectedDistances[j];
                    j = j - 1;
                }
                supportSelectedIndices[j + 1] = index;
                supportOutputMinima[j + 1] = minimum;
                supportOutputMaxima[j + 1] = maximum;
                supportSelectedDistances[j + 1] = distance;
            }
            if (!motor.SetSupportProps(supportOutputMinima, supportOutputMaxima, written))
            {
                if (supportLogCount < BlockingLogLimit)
                {
                    LogSupportOnce("[PickupFieldHost] motor refused the support set: "
                        + motor.GetSupportPropRefusalReason());
                }
                return;
            }

            int[] swap = supportPreviousIndices;
            supportPreviousIndices = supportSelectedIndices;
            supportSelectedIndices = swap;
            previousSupportCount = written;
            supportAppliedCount = written;

            if (written > 0 && !loggedSupportSet)
            {
                loggedSupportSet = true;
                Debug.Log("[PickupFieldHost] support props=" + written
                    + " candidates=" + supportCandidateCount
                    + " supportY=" + motor.GetSupportSurfaceHeightWorldM()
                    + " stepLimit=" + motor.GetStepHeightLimitWorldM()
                    + " capacity=" + capacity);
            }
        }

        /// <summary>Builds, validates and registers the blocking selection of this scan.</summary>
        private void UpdateBlockingSelection(Vector3 center, float radius, int capacity,
            float ballMm)
        {
            if (capacity <= 0) return;
            int written = BlockingPropMath.SelectNearest(center, radius,
                propMinima, propMaxima, blockingCandidate, propCount,
                blockingPreviousIndices, previousBlockingCount, blockingStickinessM,
                blockingSelectedIndices, blockingSelectedDistances, capacity);
            if (written < 0)
            {
                LogBlockingOnce("[PickupFieldHost] blocking selection refused its input;"
                    + " keeping the previous box set.");
                return;
            }

            for (int i = 0; i < written; i++)
            {
                int index = blockingSelectedIndices[i];
                blockingOutputMinima[i] = propMinima[index];
                blockingOutputMaxima[i] = propMaxima[index];
            }
            if (!motor.SetBlockingBoxes(blockingOutputMinima, blockingOutputMaxima, written))
            {
                if (blockingLogCount < BlockingLogLimit)
                {
                    LogBlockingOnce("[PickupFieldHost] motor refused the blocking set: "
                        + motor.GetBlockingBoxRefusalReason());
                }
                return;
            }

            // Swap rather than copy: SelectNearest needs last scan's order to apply
            // stickiness, and this scan's answer becomes next scan's "previous".
            int[] swap = blockingPreviousIndices;
            blockingPreviousIndices = blockingSelectedIndices;
            blockingSelectedIndices = swap;
            previousBlockingCount = written;
            blockingAppliedCount = written;

            if (written > 0 && !loggedBlockingSet)
            {
                loggedBlockingSet = true;
                Debug.Log("[PickupFieldHost] blocking boxes=" + written
                    + " candidates=" + blockingCandidateCount
                    + " ballMm=" + ballMm
                    + " radiusM=" + radius
                    + " capacity=" + capacity);
            }
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
        /// One predicate, four sets. The candidate flags are the ones
        /// <see cref="UpdatePropObstacles"/> already computed with the absorb gate's own
        /// predicate (<see cref="PickupAbsorbMath.IsBeyondAbsorbLimit"/>) and the
        /// motor's step limit, so a prop cannot be a wall in one model and a floor in the
        /// other. The selection reuses <see cref="BlockingPropMath.SelectNearest"/> - the
        /// same nearest-first plus stickiness rule the v1 sets use - over the merged
        /// candidates, with its own 12-slot capacity (6 blocking + 6 support, one table,
        /// because a prop is exactly one of the two per scan).
        /// </para>
        /// <para>
        /// The box is the prop's own: <c>propLocalBounds</c> rotated once by the prop's world
        /// rotation (<see cref="PropObbBakeMath.TryBakeProp"/>), not the world AABB rotated
        /// again. A row without a usable local column falls back to the world AABB as an
        /// axis-aligned box - bit-for-bit the v1 shape - and is counted so the fallback is
        /// visible instead of silent.
        /// </para>
        /// <para>
        /// While the motor reports v1 this does nothing and clears what it had: the v1 sets
        /// are the whole story then, and a stale v2 set would be wrong the moment the flag
        /// flips back.
        /// </para>
        /// </remarks>
        private void RefreshPropObbs(Vector3 center, float radiusM)
        {
            if (motor == null) return;
            if (propObbCandidate == null) return;
            if (!motor.IsContactModelV2())
            {
                // v1: the v1 tables are the whole world, and a block this host registered
                // while the flag was on must not survive the flip.
                ClearPropObbs();
                ClearPropProxies();
                return;
            }

            // The block's capacity is the motor's; the host's buffer is sized for the same
            // 12 slots, and a motor that ever offers fewer is honoured instead of being sent
            // a batch it can only refuse.
            int capacity = motor.GetPropObbsCapacity();
            if (capacity > PropObbBakeMath.PropObbCapacity) capacity = PropObbBakeMath.PropObbCapacity;
            if (capacity <= 0) { ClearPropObbs(); ClearPropProxies(); return; }

            int candidates = 0;
            for (int i = 0; i < propCount; i++)
            {
                bool candidate = blockingCandidate[i] || supportCandidate[i];
                propObbCandidate[i] = candidate;
                if (candidate) candidates = candidates + 1;
            }
            propObbCandidateCount = candidates;
            // More candidates than the block holds is the §5.4 failure mode: count it once per
            // scan so it is visible, and let the nearest-first selection keep the nearest.
            if (candidates > capacity) propObbOverflowCount = propObbOverflowCount + 1;

            int written = BlockingPropMath.SelectNearest(center, radiusM, propMinima, propMaxima,
                propObbCandidate, propCount, propObbPreviousIndices, propObbPreviousCount,
                blockingStickinessM, propObbSelectedIndices, propObbSelectedDistances,
                capacity);
            if (written < 0)
            {
                // A refused selection is a data error, not an empty world: keep the solids
                // that are registered and say nothing per scan (the v1 selection does the same).
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
        /// Only called while <c>IsContactModelV2()</c> (the caller gates it): the motor
        /// ignores prop OBBs with the flag off, so spending a 12-slot validate+copy per scan
        /// on them would be pure cost. The CLEAR is deliberately not gated - it is one call
        /// per set change and it is what stops a block left behind by an earlier flag-on
        /// frame from coming back when the flag flips on again.
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
                return;
            }
            if (!motor.IsContactModelV2()) return;
            if (motor.SetPropObbs(propObbCenter, propObbAxisX, propObbAxisY, propObbAxisZ,
                propObbHalfExtents, propObbSurfaceId, propObbRole, count))
            {
                propObbAppliedCount = count;
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
                // Unconditional: the motor ignores the call with the flag off, and a block
                // left behind would be stale the moment the flag flips on.
                motor.ClearPropObbs();
                propObbAppliedCount = 0;
            }
            if (propObbCount == 0 && propObbPreviousCount == 0) return;
            propObbCount = 0;
            propObbPreviousCount = 0;
            propObbCandidateCount = 0;
            propObbFallbackRows = 0;
        }

        // Drops both motor sets and this host's memory of them. Used where a set cannot be
        // right any more, never as a failure response.
        private void ClearPropSets()
        {
            // The motor calls are the only part worth skipping: each own clear is a no-op
            // when nothing is registered.
            if (blockingAppliedCount > 0 || previousBlockingCount > 0)
                motor.ClearBlockingBoxes();
            if (supportAppliedCount > 0 || previousSupportCount > 0)
                motor.ClearSupportProps();
            blockingAppliedCount = 0;
            blockingCandidateCount = 0;
            previousBlockingCount = 0;
            supportAppliedCount = 0;
            supportCandidateCount = 0;
            previousSupportCount = 0;
            ClearPropObbs();
            ClearPropProxies();
        }

        // One warning per category, ever: a field with a bad capacity must not flood the
        // console at the scan cadence. Callers guard the message build with the same count.
        private void LogBlockingOnce(string message)
        {
            if (blockingLogCount >= BlockingLogLimit) return;
            blockingLogCount = blockingLogCount + 1;
            Debug.LogWarning(message);
        }

        private void LogSupportOnce(string message)
        {
            if (supportLogCount >= BlockingLogLimit) return;
            supportLogCount = supportLogCount + 1;
            Debug.LogWarning(message);
        }

        /// <summary>
        /// Blocking boxes registered with the motor by the last scan (0..capacity).
        /// </summary>
        public int GetBlockingPropCount()
        {
            return blockingAppliedCount;
        }

        /// <summary>
        /// Props that were blocking candidates in the last scan: free, usable volume,
        /// inside the pruning bound, absorb-proof, and above the step limit.
        /// </summary>
        public int GetBlockingCandidateCount()
        {
            return blockingCandidateCount;
        }

        /// <summary>Boxes the motor can take at once, as read by the last scan (0 before Start).</summary>
        public int GetBlockingPropCapacity()
        {
            return blockingCapacity;
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

        /// <summary>Support tops registered with the motor by the last scan (0..capacity).</summary>
        public int GetSupportPropCount()
        {
            return supportAppliedCount;
        }

        /// <summary>
        /// Props that were support candidates in the last scan: free, usable volume,
        /// inside the pruning bound, absorb-proof, and within the step limit.
        /// </summary>
        public int GetSupportPropCandidateCount()
        {
            return supportCandidateCount;
        }

        /// <summary>Support tops the motor can take at once, as read by the last scan.</summary>
        public int GetSupportPropCapacity()
        {
            return supportCapacity;
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
