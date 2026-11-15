using UdonSharp;
using UnityEngine;
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
    /// commit, so a host that queues on proximity can pin all four slots with props the
    /// ball has already rolled past. This host therefore (a) submits only props inside
    /// <c>activationRadiusM</c>, nearest first and only while the queue has a free slot,
    /// so it never causes the motor's data-loss overflow latch, and (b) withdraws a prop
    /// once it is beyond <c>releaseRadiusM</c>. The queue stays the single owner of "is
    /// this queued": the per-prop submit time is only a cheap filter on whether it is
    /// worth calling <c>WithdrawCandidate</c> again, and it is cleared whenever that call
    /// reports it removed something.
    /// </para>
    /// <para>
    /// Attach or hide, never both. A prop whose pose the math refuses is hidden
    /// (<c>SetActive(false)</c>) and logged once with a reason code, because a prop that
    /// was absorbed but cannot be attached must not keep floating in the world. The two
    /// counts are separate: only attached props count toward the per-kind tally the story
    /// level's category goal will read.
    /// </para>
    /// <para>
    /// Blocking props. A prop the ball cannot absorb yet must stop the ball instead of
    /// letting it roll through, and the ball's only collision authority is the motor's
    /// sweep, so this host registers the nearby absorb-proof props as the motor's dynamic
    /// blocking boxes. The predicate is the absorb gate's own volume limit
    /// (<see cref="PickupAbsorbMath.IsBeyondAbsorbVolumeLimit"/> with the motor's
    /// <c>GetAbsorbVolumeLimitWorldM3</c>), never a second threshold: the instant the ball
    /// grows past a prop's volume, the same function stops calling it "too big", the next
    /// scan drops its box, and the prop becomes absorbable. Boxes come from the gameplay
    /// volume <c>propBounds</c> around the prop's world position - not from a
    /// <c>BoxCollider</c> and not from the visual mesh - because the three layers are
    /// separate and most scene props are authored without a collider at all
    /// (Docs/Design/PickupAbsorb/BLOCKING_PROPS_PLAN.md). Absorbed props are never
    /// candidates, and a field reset drops the set.
    /// </para>
    /// <para>
    /// One owner. The blocking set is a single slot on the motor, so only ONE field host
    /// may write it per motor; two hosts would overwrite each other's work (last writer
    /// wins) and each would believe its own set was registered. There is no runtime
    /// arbiter - a second writer is a scene wiring error, checked in the editor.
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

        // The value unit the ledger consumes must be a whole number at least 1
        // (Docs/OPEN_DECISIONS.md (j)); a fractional or zero value is refused at commit
        // time as RejectNoGain, which is indistinguishable from "never touched" from
        // outside. The host therefore reports such rows once at start-up.
        private const int MinValueUnits = 1;

        private const float DefaultScanIntervalSeconds = 0.05f;
        private const float DefaultActivationRadiusM = 1.6f;
        private const float DefaultReleaseRadiusM = 2.5f;

        // The motor's blocking capacity is six boxes. This buffer is deliberately larger,
        // same reason as SelectionCapacity: a bigger motor capacity then needs no change
        // here, and an unexpected one is clamped with a single log.
        private const int BlockingSelectionCapacity = 8;

        // How far past the ball's collision radius a blocking prop may be and still be
        // registered. It covers one scan interval of travel with slack - 4 m/s * 0.05 s
        // = 0.2 m, so 0.8 m leaves room for a slower scan or a higher speed - and is not a
        // gameplay threshold: a prop closer than this is a wall, a prop farther is simply
        // not registered yet (BLOCKING_PROPS_PLAN.md §2 A).
        private const float DefaultBlockingMarginM = 0.8f;

        // A prop already in the set stays until it is clearly out of the margin, so a prop
        // hovering at the edge does not add and remove its box every scan. Purely a
        // hysteresis on the selection, not a second "is it blocking" rule.
        private const float DefaultBlockingStickinessM = 0.25f;

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
        [SerializeField] private Vector3[] propBounds;
        [SerializeField] private int[] valueUnits;
        // Optional: wired colliders are disabled on attach so an absorbed prop cannot block
        // anything. Leave the array empty to skip that.
        [SerializeField] private Collider[] propColliders;

        [Header("Radii and cadence")]
        [SerializeField] private float activationRadiusM = DefaultActivationRadiusM;
        [SerializeField] private float releaseRadiusM = DefaultReleaseRadiusM;
        [SerializeField] private float scanIntervalSeconds = DefaultScanIntervalSeconds;

        [Header("Blocking props (absorb-proof props stop the ball)")]
        // Named as the DISABLED flag on purpose: a field that is absent from an already
        // serialized scene comes in at the CLR default (false), and "false" must mean
        // "blocking works". With `blockingEnabled = true` a stale serialization could
        // silently switch the whole feature off, and the symptom would be exactly the bug
        // this file exists to fix. Therefore this flag defaults to false = enabled.
        [SerializeField] private bool blockingDisabled = false;
        [SerializeField] private float blockingMarginM = DefaultBlockingMarginM;
        [SerializeField] private float blockingStickinessM = DefaultBlockingStickinessM;

        // ---- Per-prop store (allocated in Start, never serialized) ----
        private int[] propStates;
        private float[] lastSubmitTimes;
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

        // Bounded nearest-first selection for one scan.
        private int[] nearestIndices;
        private float[] nearestDistSq;

        // ---- Blocking-prop store (allocated in Start, never serialized) ----
        // One row per prop, indexed like every other parallel array. blockingUsable and
        // blockingHalfDiagonal are fixed in Start (both depend only on propBounds, which
        // does not change); the boxes are rebuilt per scan from the prop's current world
        // position so a prop another system moved cannot leave a stale wall behind.
        private bool[] blockingUsable;
        private float[] blockingHalfDiagonal;
        private Vector3[] blockingMinima;
        private Vector3[] blockingMaxima;
        // Per-scan candidate flags, written for every prop so a stale true cannot survive.
        private bool[] blockingCandidate;
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
        private int blockingUsableCount;
        private float nextBlockingScanTime;
        private int blockingLogCount;

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

        private void Start()
        {
            ClampSettings();
            ResolvePropCount();
            AllocatePropStore();
            RecordOriginalPoses();
            MarkSubmittable();
            PrepareBlockingRows();
            ReportPropDataProblems();
            seenEvents = 0;
            nextScanTime = 0f;
            nextBlockingScanTime = 0f;
            Debug.Log("[PickupFieldHost] ready props=" + propCount
                + " submittable=" + submittableCount
                + " blockingRows=" + blockingUsableCount
                + " blocking=" + (blockingDisabled ? "off" : "on")
                + " activationR=" + activationRadiusM
                + " releaseR=" + releaseRadiusM
                + " scanInterval=" + scanIntervalSeconds);
        }

        /// <summary>
        /// Reads the absorbed-event ring, refreshes the blocking set, and while collecting
        /// offers nearby props.
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
        /// The blocking set is refreshed BEFORE the collecting gate and before the
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

            ConsumeAbsorbedEvents();
            UpdateBlockingProps();
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
        /// Blocking is deliberately NOT affected: a stopped round is still a solid world, so
        /// this only withdraws candidates. The prop that stopped the ball keeps stopping it,
        /// which is what makes a time-out feel like a frozen game rather than a ghost level.
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
            // Every submit stamp goes, not just the moved rows: the caller has already had
            // the motor clear its candidate queue, so no id this host stamped is queued
            // anywhere and the next scan starts from a clean "did I submit" filter.
            for (int i = 0; i < propCount; i++) lastSubmitTimes[i] = 0f;

            // The blocking set is a function of where the ball is, so nothing about it
            // survives a reset; static planes and boxes stay in the motor (the map did not
            // move). Clearing here as well as in TeleportTo covers a reset that does not
            // teleport, and the next scan rebuilds from the restored field.
            if (motor != null) motor.ClearBlockingBoxes();
            blockingAppliedCount = 0;
            blockingCandidateCount = 0;
            previousBlockingCount = 0;
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
            if (!KatamariMath.IsFiniteFloat(releaseRadiusM) || releaseRadiusM <= 0f)
                releaseRadiusM = DefaultReleaseRadiusM;
            // Release must be the outer ring; the lab's per-prop host has it inverted
            // (submit at 1.6 m, release past 1.0 m), which withdraws a prop in the same
            // frame it was queued. That is a lab setting, not a contract, so it is not
            // copied: here the gap is the hysteresis that keeps a queued prop queued.
            if (releaseRadiusM <= activationRadiusM) releaseRadiusM = activationRadiusM * 1.25f;
            if (!KatamariMath.IsFiniteFloat(scanIntervalSeconds) || scanIntervalSeconds <= 0f)
                scanIntervalSeconds = DefaultScanIntervalSeconds;
            if (!KatamariMath.IsFiniteFloat(blockingMarginM) || blockingMarginM < 0f)
                blockingMarginM = DefaultBlockingMarginM;
            if (!KatamariMath.IsFiniteFloat(blockingStickinessM) || blockingStickinessM < 0f)
                blockingStickinessM = DefaultBlockingStickinessM;
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
            propCount = Smaller(propCount, LengthOfValues());
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

        private int LengthOfValues()
        {
            if (valueUnits == null) return 0;
            return valueUnits.Length;
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
            lastSubmitTimes = new float[propCount];
            originalParents = new Transform[propCount];
            originalLocalPositions = new Vector3[propCount];
            originalLocalRotations = new Quaternion[propCount];
            originalActiveStates = new bool[propCount];
            submittable = new bool[propCount];
            nearestIndices = new int[SelectionCapacity];
            nearestDistSq = new float[SelectionCapacity];
            blockingUsable = new bool[propCount];
            blockingHalfDiagonal = new float[propCount];
            blockingMinima = new Vector3[propCount];
            blockingMaxima = new Vector3[propCount];
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
            blockingUsableCount = 0;
            blockingLogCount = 0;
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

        private bool IsSubmittable(int index)
        {
            if (propTransforms[index] == null) return false;
            // SubmitCandidate refuses an empty id and the gate refuses an empty kind text.
            if (itemIds[index] == null) return false;
            if (itemIds[index].Length == 0) return false;
            if (kinds[index] == null) return false;
            if (kinds[index].Length == 0) return false;
            // A value the ledger gain mapping refuses (zero, fractional, negative, huge) is
            // RejectNoGain at commit time, which is also "stays queued": never offer it.
            if (valueUnits[index] < MinValueUnits) return false;
            Vector3 bounds = propBounds[index];
            if (!KatamariMath.IsFiniteVector(bounds)) return false;
            if (bounds.x < 0f || bounds.y < 0f || bounds.z < 0f) return false;
            return true;
        }

        /// <summary>
        /// Fixes which rows can ever block (a transform, and a positive finite gameplay
        /// volume) and how far each one's box can reach from its centre.
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
        /// <see cref="UpdateBlockingProps"/>: for any point, the distance to an
        /// axis-aligned box is at least the distance to its centre minus its half diagonal.
        /// It depends only on propBounds, so it is computed once here.
        /// </para>
        /// </remarks>
        private void PrepareBlockingRows()
        {
            blockingUsableCount = 0;
            for (int i = 0; i < propCount; i++)
            {
                blockingUsable[i] = false;
                blockingHalfDiagonal[i] = 0f;
                if (propTransforms[i] == null) continue;
                Vector3 bounds = propBounds[i];
                if (!KatamariMath.IsFiniteVector(bounds)) continue;
                // A zero-volume prop can never exceed any volume limit, so it can never
                // block; requiring strictly positive sizes keeps this row's box out of the
                // maths entirely instead of relying on that fact downstream.
                if (bounds.x <= 0f || bounds.y <= 0f || bounds.z <= 0f) continue;

                float halfX = bounds.x * 0.5f;
                float halfY = bounds.y * 0.5f;
                float halfZ = bounds.z * 0.5f;
                float halfDiagonal = Mathf.Sqrt(halfX * halfX + halfY * halfY + halfZ * halfZ);
                if (!KatamariMath.IsFiniteFloat(halfDiagonal)) continue;
                blockingHalfDiagonal[i] = halfDiagonal;
                blockingUsable[i] = true;
                blockingUsableCount = blockingUsableCount + 1;
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
            int badValues = 0;
            int degenerateBounds = 0;
            for (int i = 0; i < propCount; i++)
            {
                if (propTransforms[i] == null) missingTransforms++;
                if (itemIds[i] == null || itemIds[i].Length == 0) emptyIds++;
                if (kinds[i] == null || kinds[i].Length == 0) emptyKinds++;
                if (valueUnits[i] < MinValueUnits) badValues++;
                Vector3 bounds = propBounds[i];
                if (!KatamariMath.IsFiniteVector(bounds)) degenerateBounds++;
                else if (bounds.x <= 0f || bounds.y <= 0f || bounds.z <= 0f) degenerateBounds++;
            }

            int longest = 0;
            if (propTransforms != null) longest = Larger(longest, propTransforms.Length);
            if (itemIds != null) longest = Larger(longest, itemIds.Length);
            if (kinds != null) longest = Larger(longest, kinds.Length);
            if (propBounds != null) longest = Larger(longest, propBounds.Length);
            if (valueUnits != null) longest = Larger(longest, valueUnits.Length);
            int ignored = longest - propCount;

            if (propTransforms == null || itemIds == null || kinds == null ||
                propBounds == null || valueUnits == null)
            {
                Debug.LogWarning("[PickupFieldHost] a prop array is null; props=" + propCount);
            }
            else if (ignored > 0)
            {
                Debug.LogWarning("[PickupFieldHost] parallel prop arrays disagree; ignoring the "
                    + ignored + " trailing entr(y/ies) of the longer arrays. props=" + propCount);
            }
            if (missingTransforms > 0 || emptyIds > 0 || emptyKinds > 0 ||
                badValues > 0 || degenerateBounds > 0)
            {
                Debug.LogWarning("[PickupFieldHost] prop data: missingTransforms=" + missingTransforms
                    + " emptyIds=" + emptyIds
                    + " emptyKinds=" + emptyKinds
                    + " valueUnitsBelowMin=" + badValues
                    + " zeroOrNegativeBounds=" + degenerateBounds
                    + " - these rows are never offered to the motor, so they can never be"
                    + " absorbed (valueUnits must be a whole number >= 1, see"
                    + " Docs/OPEN_DECISIONS.md (j))");
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

            propStates[index] = StateAttached;
            lastSubmitTimes[index] = 0f;
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
            lastSubmitTimes[index] = 0f;
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
        // One proximity scan: release, then offer the nearest props to the queue.
        // ------------------------------------------------------------------

        /// <summary>
        /// Withdraws props that left the release radius and queues the nearest free props
        /// inside the activation radius, nearest first and only while the queue has room.
        /// </summary>
        /// <remarks>
        /// One pass over the props does both jobs, so the cost per scan is one distance per
        /// prop rather than one per prop per queue slot. Nearest-first matters: the motor's
        /// queue is four slots, and filling them with the four lowest array indices is how a
        /// host starves the item the ball is actually touching. Submitting into a full queue
        /// is refused and latches the motor's overflow signal, which means "the host lost a
        /// candidate", so the slot count is checked before every submit.
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

            float activationSq = activationRadiusM * activationRadiusM;
            float releaseSq = releaseRadiusM * releaseRadiusM;

            int selected = 0;
            for (int i = 0; i < propCount; i++)
            {
                if (propStates[i] != StateFree) continue;
                if (!submittable[i]) continue;
                Transform prop = propTransforms[i];
                if (prop == null) continue;

                Vector3 delta = prop.position - center;
                float distSq = delta.x * delta.x + delta.y * delta.y + delta.z * delta.z;
                if (!KatamariMath.IsFiniteFloat(distSq)) continue;

                if (distSq > releaseSq)
                {
                    // Only worth a call if this host ever queued it; the motor holds the
                    // truth, this is only a "did I submit once" filter.
                    if (lastSubmitTimes[i] > 0f)
                    {
                        if (motor.WithdrawCandidate(itemIds[i])) lastSubmitTimes[i] = 0f;
                    }
                    continue;
                }
                if (distSq > activationSq) continue;

                selected = InsertNearest(i, distSq, selected, slotCount);
            }

            for (int i = 0; i < selected; i++)
            {
                if (motor.GetPendingCandidateCount() >= slotCount) break;
                SubmitProp(nearestIndices[i]);
            }
        }

        /// <summary>
        /// Keeps the nearest props seen so far, ascending by squared distance.
        /// </summary>
        /// <remarks>
        /// Insertion into two small caller-owned arrays, no allocation and no sort of the
        /// whole prop list. Returns the new number of selected entries.
        /// </remarks>
        private int InsertNearest(int index, float distSq, int selected, int limit)
        {
            if (limit <= 0) return selected;
            if (limit > SelectionCapacity) limit = SelectionCapacity;

            if (selected < limit)
            {
                int at = selected;
                while (at > 0 && nearestDistSq[at - 1] > distSq)
                {
                    nearestDistSq[at] = nearestDistSq[at - 1];
                    nearestIndices[at] = nearestIndices[at - 1];
                    at--;
                }
                nearestDistSq[at] = distSq;
                nearestIndices[at] = index;
                return selected + 1;
            }

            if (distSq >= nearestDistSq[limit - 1]) return selected;
            int pos = limit - 1;
            while (pos > 0 && nearestDistSq[pos - 1] > distSq)
            {
                nearestDistSq[pos] = nearestDistSq[pos - 1];
                nearestIndices[pos] = nearestIndices[pos - 1];
                pos--;
            }
            nearestDistSq[pos] = distSq;
            nearestIndices[pos] = index;
            return selected;
        }

        /// <summary>
        /// Queues one prop. The motor de-duplicates by item id, so a repeat is a no-op that
        /// consumes no slot; that is why the slot count is re-read rather than assumed.
        /// </summary>
        private void SubmitProp(int index)
        {
            Vector3 itemPosition = propTransforms[index].position;
            motor.SubmitCandidate(true, kinds[index], propBounds[index],
                itemPosition, (float)valueUnits[index], itemIds[index], 0);
            // Stamped even when the motor answered "already queued": both cases leave this
            // host willing to withdraw the id later if the ball leaves.
            lastSubmitTimes[index] = Time.time;
        }

        private void WithdrawSubmitted()
        {
            if (motor == null) return;
            for (int i = 0; i < propCount; i++)
            {
                if (propStates[i] != StateFree) continue;
                if (lastSubmitTimes[i] <= 0f) continue;
                if (motor.WithdrawCandidate(itemIds[i])) lastSubmitTimes[i] = 0f;
            }
        }

        // ------------------------------------------------------------------
        // Blocking props: the nearby absorb-proof props become sweep obstacles.
        // ------------------------------------------------------------------

        /// <summary>
        /// Rebuilds the motor's dynamic blocking set from the nearby props the ball cannot
        /// absorb yet, once per scan interval.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Cost per scan with N props: one transform read and one squared centre distance
        /// per prop; then, only for the handful inside the pruning bound, one volume
        /// comparison and (for the absorb-proof ones) one box plus one AABB distance. The
        /// selection is <see cref="BlockingPropMath.SelectNearest"/>: one distance per
        /// candidate plus at most capacity (6) passes over the candidates. Nothing is
        /// allocated after <c>Start</c>, and the motor acknowledges an unchanged box set
        /// without rebuilding its sweep arrays, so a stationary ball costs one no-op
        /// compare per scan.
        /// </para>
        /// <para>
        /// The predicate is the absorb gate's, not a copy of it: the limit comes from the
        /// motor (<c>GetAbsorbVolumeLimitWorldM3</c> - the same value the gate prices) and
        /// the test is <see cref="PickupAbsorbMath.IsBeyondAbsorbVolumeLimit"/>, the same
        /// volume comparison the gate makes. A prop therefore stops being a wall and starts
        /// being absorbable at one instant, and no second threshold exists to drift.
        /// </para>
        /// <para>
        /// Boxes come from the gameplay volume <c>propBounds</c> around the prop's current
        /// world position - never from a collider and never from the visual mesh. Rotation
        /// is not applied (an axis-aligned box around the authored volume), so a turned prop
        /// can block slightly early; that is the documented AABB choice, not an oversight.
        /// </para>
        /// <para>
        /// Failures keep the registered set instead of clearing it: an empty set is exactly
        /// the "ball rolls through the world" bug this method exists to remove. The set is
        /// cleared only where it cannot be right any more - blocking disabled, the motor
        /// refusing to price its own limit, or a field reset.
        /// </para>
        /// </remarks>
        private void UpdateBlockingProps()
        {
            if (blockingDisabled)
            {
                if (blockingAppliedCount > 0)
                {
                    motor.ClearBlockingBoxes();
                    blockingAppliedCount = 0;
                    previousBlockingCount = 0;
                    blockingCandidateCount = 0;
                }
                return;
            }

            float now = Time.time;
            if (now < nextBlockingScanTime) return;
            nextBlockingScanTime = now + scanIntervalSeconds;

            int capacity = motor.GetBlockingBoxCapacity();
            blockingCapacity = capacity;
            if (capacity <= 0) return;
            if (capacity > BlockingSelectionCapacity)
            {
                if (blockingLogCount < BlockingLogLimit)
                {
                    LogBlockingOnce("[PickupFieldHost] motor blocking capacity " + capacity
                        + " exceeds this host's selection buffer " + BlockingSelectionCapacity
                        + "; only the nearest " + BlockingSelectionCapacity
                        + " are registered per scan.");
                }
                capacity = BlockingSelectionCapacity;
            }

            float limit = motor.GetAbsorbVolumeLimitWorldM3();
            if (!KatamariMath.IsFiniteFloat(limit) || limit < 0f)
            {
                // The motor could not price its own limit. Registering nothing matches what
                // the gate does with a non-finite limit (it refuses every item), so the two
                // answers stay identical; the motor already reports the refused derive.
                ClearBlockingSet();
                return;
            }

            Vector3 center = motor.GetCenterWorld();
            if (!KatamariMath.IsFiniteVector(center)) return;
            float radius = motor.GetCollisionRadiusWorldM() + blockingMarginM;
            if (!KatamariMath.IsFiniteFloat(radius) || radius <= 0f) return;
            // Wider than the selection radius, because SelectNearest keeps a previous entry
            // out to radius + stickiness. Pruning must never see less than the selection
            // can keep, or stickiness would silently degrade to "re-pick every scan".
            float pruneRadius = radius + blockingStickinessM;
            if (!KatamariMath.IsFiniteFloat(pruneRadius)) return;

            int candidateCount = 0;
            for (int i = 0; i < propCount; i++)
            {
                blockingCandidate[i] = false;
                // Absorbed and hidden props are no longer walls. A row with no transform or
                // no usable volume has no box to build, so it is skipped too (not because
                // it is unsubmittable: a prop the motor would refuse as a candidate is
                // still a solid object, and PrepareBlockingRows keeps it).
                if (propStates[i] != StateFree) continue;
                if (!blockingUsable[i]) continue;

                Vector3 position = propTransforms[i].position;
                float dx = position.x - center.x;
                float dy = position.y - center.y;
                float dz = position.z - center.z;
                float distanceSq = dx * dx + dy * dy + dz * dz;
                if (!KatamariMath.IsFiniteFloat(distanceSq)) continue;

                // Pruning only, never a decision: the distance to an axis-aligned box is at
                // least the distance to its centre minus its half diagonal, so a prop past
                // this bound cannot be selected at radius + stickiness.
                float pruneLimit = pruneRadius + blockingHalfDiagonal[i];
                if (distanceSq > pruneLimit * pruneLimit) continue;

                if (!PickupAbsorbMath.IsBeyondAbsorbVolumeLimit(propBounds[i], limit)) continue;

                Vector3 minimum;
                Vector3 maximum;
                if (!BlockingPropMath.TryBoxFromCentreAndExtents(position, propBounds[i],
                    out minimum, out maximum)) continue;
                blockingMinima[i] = minimum;
                blockingMaxima[i] = maximum;
                blockingCandidate[i] = true;
                candidateCount = candidateCount + 1;
            }
            blockingCandidateCount = candidateCount;

            int written = BlockingPropMath.SelectNearest(center, radius,
                blockingMinima, blockingMaxima, blockingCandidate, propCount,
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
                blockingOutputMinima[i] = blockingMinima[index];
                blockingOutputMaxima[i] = blockingMaxima[index];
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
                    + " candidates=" + candidateCount
                    + " limitM3=" + limit
                    + " radiusM=" + radius
                    + " capacity=" + capacity);
            }
        }

        // Drops both the motor's set and this host's memory of it. Used where the set
        // cannot be right any more, never as a failure response.
        private void ClearBlockingSet()
        {
            // The motor call is the only part worth skipping: its own clear is a no-op
            // when nothing is registered.
            if (blockingAppliedCount > 0 || previousBlockingCount > 0)
                motor.ClearBlockingBoxes();
            blockingAppliedCount = 0;
            blockingCandidateCount = 0;
            previousBlockingCount = 0;
        }

        // One warning per category, ever: a field with a bad capacity must not flood the
        // console at the scan cadence. Callers guard the message build with the same count.
        private void LogBlockingOnce(string message)
        {
            if (blockingLogCount >= BlockingLogLimit) return;
            blockingLogCount = blockingLogCount + 1;
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
        /// inside the pruning bound, and beyond the absorb volume limit.
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

        /// <summary>Whether this field registers blocking boxes at all.</summary>
        public bool IsBlockingEnabled()
        {
            return !blockingDisabled;
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
