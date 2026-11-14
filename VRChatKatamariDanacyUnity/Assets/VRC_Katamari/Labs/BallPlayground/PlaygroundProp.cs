using UdonSharp;
using UnityEngine;
using VRCKatamari.BallMotor;

namespace VRCKatamari.Labs.BallPlayground
{
    /// <summary>
    /// One absorbable prop in the ball playground: it queues itself as a pickup
    /// candidate, then hides itself when the motor reports that its own item id
    /// was committed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Why this exists at all. The motor owns the pickup decision but not the
    /// scene: the pickup layer's contract leaves candidate collection to the
    /// host, and the motor holds only four candidates at a time. This component
    /// is that host for one prop, deliberately dumb - it submits, it waits, it
    /// hides.
    /// </para>
    /// <para>
    /// The queue is four slots. A prop whose submit is refused retries on a
    /// timer instead of dropping the item, so the first four props do not
    /// permanently monopolise the queue while the ball is elsewhere: as the ball
    /// eats one, a slot frees and the next waiting prop fills it. A prop that
    /// was accepted never submits again - re-submitting the same item id would
    /// put a second copy in the queue, and the ledger's per-item claim is
    /// one-shot, so the duplicate could never commit and would block a slot.
    /// </para>
    /// <para>
    /// Hiding, not destroying: the renderer and the collider are switched off so
    /// a second Play Mode run starts from the same scene without a rebuild.
    /// </para>
    /// </remarks>
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class PlaygroundProp : UdonSharpBehaviour
    {
        [SerializeField] private KatamariBallMotor motor;
        [SerializeField] private GameObject visualRoot;
        [SerializeField] private Collider bodyCollider;

        [Header("Candidate payload")]
        [SerializeField] private string itemId = "prop-00";
        [SerializeField] private string kind = "small";
        [SerializeField] private float semanticDiameterM = 0.2f;
        [SerializeField] private float valueUnits = 3f;
        [SerializeField] private bool absorbable = true;

        [Header("Retry")]
        [SerializeField] private float retrySeconds = 0.25f;
        [SerializeField] private float activationRadiusM = 1.6f;
        // Host admission contract (Docs of the pickup layer): a queued candidate is
        // never removed by the motor except on a successful commit, so the host owns
        // the lifecycle. Holding a slot until 3.5 m let four already-passed light
        // props monopolise the queue and heavier props never got in at all. Withdraw
        // at roughly 2 x reach + margin so the four slots keep rotating onto whatever
        // is nearest right now.
        [SerializeField] private float releaseRadiusM = 1.0f;

        private bool queued;
        private bool consumed;
        private bool started;
        private int seenEvents;
        private float nextRetryTime;

        public void Start()
        {
            // Deliberately does no motor work: Unity does not promise that this
            // component's Start runs after the motor's, and the motor allocates its
            // candidate stores in Start. The first Update is after every Start.
            seenEvents = 0;
            nextRetryTime = 0f;
        }

        public void Update()
        {
            if (motor == null) return;
            if (consumed) return;

            if (!started)
            {
                started = true;
                TryQueue();
                return;
            }

            if (!queued && Time.time >= nextRetryTime)
            {
                nextRetryTime = Time.time + retrySeconds;
                TryQueue();
            }
            if (queued)
            {
                ReleaseIfLeftBehind();
                ScanAbsorbedEvents();
            }
        }

        /// <summary>
        /// Queues this prop once the ball is near and the motor has a free slot.
        /// </summary>
        /// <remarks>
        /// The motor holds four candidates until each is absorbed, and it has no way
        /// to withdraw one. Submitting from every prop at start therefore let four
        /// far-away props occupy the queue for the whole run and nothing was ever
        /// eaten. Two guards fix that: submit only inside the activation radius, and
        /// only while a slot is free (submitting into a full queue is refused and
        /// latches the motor's data-loss overflow signal, which a polling host must
        /// not cause).
        /// </remarks>
        private void TryQueue()
        {
            if (motor.GetPendingCandidateCount() >= motor.GetPendingCandidateCapacity()) return;
            Vector3 fromBall = transform.position - motor.GetCenterWorld();
            if (fromBall.magnitude > activationRadiusM) return;

            Vector3 bounds = new Vector3(semanticDiameterM, semanticDiameterM, semanticDiameterM);
            queued = motor.SubmitCandidate(absorbable, kind, bounds, transform.position,
                valueUnits, itemId, 0);
        }

        /// <summary>
        /// Gives the queue slot back when the ball has clearly driven away.
        /// </summary>
        /// <remarks>
        /// Without this, a prop that queued on proximity keeps one of the motor's four
        /// slots forever: the motor never removes a non-absorbed candidate, so the
        /// far-away item is refused out-of-reach on every step and no nearer prop can
        /// ever queue. Releasing lets it queue again on a later pass.
        /// </remarks>
        private void ReleaseIfLeftBehind()
        {
            Vector3 fromBall = transform.position - motor.GetCenterWorld();
            if (fromBall.magnitude <= releaseRadiusM) return;
            if (motor.WithdrawCandidate(itemId)) queued = false;
        }

        /// <summary>
        /// Walks the motor's absorbed events forward from this prop's own cursor and
        /// hides itself when it finds its id. The cursor is absolute, so events that
        /// the ring already evicted are skipped rather than mis-read.
        /// </summary>
        private void ScanAbsorbedEvents()
        {
            int total = motor.GetAbsorbedEventCount();
            if (total == seenEvents) return;

            int first = seenEvents;
            int oldestRetained = total - motor.GetAbsorbedEventCapacity();
            if (oldestRetained > first) first = oldestRetained;

            for (int i = first; i < total; i++)
            {
                string absorbedId = motor.GetAbsorbedEventItemId(i);
                if (absorbedId == null) continue;
                if (absorbedId != itemId) continue;
                Consume();
                return;
            }
            seenEvents = total;
        }

        private void Consume()
        {
            consumed = true;
            if (visualRoot != null) visualRoot.SetActive(false);
            if (bodyCollider != null) bodyCollider.enabled = false;
        }

        // ---- Read-only seams for the bootstrap's HUD and for tests ----

        public bool IsConsumed()
        {
            return consumed;
        }

        public bool IsQueued()
        {
            return queued;
        }

        public string GetItemId()
        {
            return itemId;
        }
    }
}
