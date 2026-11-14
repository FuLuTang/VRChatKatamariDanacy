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

        // ---- Per-prop store (allocated in Start, never serialized) ----
        private int[] propStates;
        private float[] lastSubmitTimes;
        // Rows the motor could only refuse forever. Queued once, such a row would sit in one
        // of the four slots and be re-refused on every step (the motor never drops a refused
        // candidate), starving the props the ball is actually touching. Fixed at start-up so
        // the scan costs one array read per prop.
        private bool[] submittable;

        // Bounded nearest-first selection for one scan.
        private int[] nearestIndices;
        private float[] nearestDistSq;

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

        private void Start()
        {
            ClampSettings();
            ResolvePropCount();
            AllocatePropStore();
            MarkSubmittable();
            ReportPropDataProblems();
            seenEvents = 0;
            nextScanTime = 0f;
            Debug.Log("[PickupFieldHost] ready props=" + propCount
                + " submittable=" + submittableCount
                + " activationR=" + activationRadiusM
                + " releaseR=" + releaseRadiusM
                + " scanInterval=" + scanIntervalSeconds);
        }

        /// <summary>
        /// Reads the absorbed-event ring and, while collecting, offers nearby props.
        /// </summary>
        /// <remarks>
        /// The ring is consumed on every frame even when collecting is off: those events
        /// belong to props that were absorbed legitimately before the stop, and dropping
        /// them would leave exactly the "prop vanished" symptom S3 exists to remove. Unity
        /// does not promise that this Update runs after the motor's, so an event may be
        /// picked up one frame late; the ring is monotonic, so that is a delay, not a loss.
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
        /// A round controller calls this with false at time-out: submitting stops and every
        /// candidate this host queued is released, so a frozen round cannot absorb anything
        /// more. Only ids this host submitted are withdrawn - <c>ClearCandidates</c> would
        /// also drop another host's work on the same motor. Props already attached are left
        /// attached: they were absorbed, and un-parenting them would be the disappearing
        /// prop again.
        /// </remarks>
        public void SetCollecting(bool collecting)
        {
            collectingEnabled = collecting;
            collectingInitialised = true;
            if (collecting) return;
            WithdrawSubmitted();
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
            submittable = new bool[propCount];
            nearestIndices = new int[SelectionCapacity];
            nearestDistSq = new float[SelectionCapacity];
            kindTallyNames = new string[propCount];
            kindTallyCounts = new int[propCount];
            kindTallyCount = 0;
            attachedCount = 0;
            hiddenCount = 0;
            lastHiddenReason = HiddenReasonNone;
            lastHiddenItemId = "";
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
