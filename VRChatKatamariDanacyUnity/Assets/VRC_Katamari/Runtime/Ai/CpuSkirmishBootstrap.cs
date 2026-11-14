using UdonSharp;
using UnityEngine;
using VRCKatamari.BallMotor;

namespace VRCKatamari.Ai
{
    /// <summary>
    /// Runs the CPU skirmish scene: one ledger and one motor per ball, four balls.
    /// </summary>
    /// <remarks>
    /// Each ball owns its own <see cref="KatamariBallMotor"/> on its own MotorHost
    /// object (the assembly constraint in Docs/Design/BallMotor/README.md: a motor
    /// must never share a GameObject with another UdonSharpBehaviour, or UdonSharp
    /// silently disables it). This host only opens each round and reports what each
    /// ball's public getters say, once per second; it never drives or resizes a ball.
    /// </remarks>
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class CpuSkirmishBootstrap : UdonSharpBehaviour
    {
        [SerializeField] private KatamariBallMotor[] motors;

        // Index-aligned with motors; a null entry is the human player's ball, which has
        // no pilot. One line per ball still carries everything about that ball.
        [SerializeField] private CpuBallPilot[] pilots;

        [Header("Ground")]
        [SerializeField] private Vector3 groundNormal = new Vector3(0f, 1f, 0f);
        [SerializeField] private float groundOffset = 0f;

        [Header("Obstacles")]
        [SerializeField] private BoxCollider[] obstacleBoxes;

        [SerializeField] private string[] allowedKinds;
        [SerializeField] private float logIntervalSeconds = 1f;

        private bool initialised;
        private int readinessFrames;
        private const int readinessLimit = 240;
        private float elapsed;
        private float nextLogTime;

        public void Start()
        {
            // Ordering note: the motor's Start may run after this one, so nothing here
            // touches motor state. Setup runs on the first Update instead.
        }

        public void Update()
        {
            if (!initialised)
            {
                // Do not touch a motor before its own Start has run. Unity does not
                // promise Start order, and AddPlane/AddBox/TryBeginPlaying on a motor
                // whose stores are still null throws, which halts that UdonBehaviour
                // for the rest of the session (symptom: every ball reads d=0 forever).
                // A positive diameter is the public proof that Start completed.
                if (!AllMotorsReady())
                {
                    readinessFrames = readinessFrames + 1;
                    if (readinessFrames == readinessLimit)
                    {
                        Debug.LogError("[skirmish] motors were not ready within " + readinessLimit
                            + " frames; refusing to drive. A motor whose Start never ran reads diameter 0.");
                    }
                    return;
                }
                Setup();
                if (!initialised) return;
            }

            elapsed = elapsed + Time.deltaTime;
            if (elapsed < nextLogTime) return;
            nextLogTime = elapsed + logIntervalSeconds;

            for (int i = 0; i < motors.Length; i++)
            {
                KatamariBallMotor m = motors[i];
                if (m == null) continue;
                string pilotPart = "";
                if (pilots != null && i < pilots.Length && pilots[i] != null)
                {
                    CpuBallPilot pilot = pilots[i];
                    pilotPart = " name=" + pilot.GetPilotName()
                        + " target=" + pilot.GetTargetName()
                        + " err=" + (Mathf.Floor(pilot.GetHorizontalErrorM() * 1000f) / 1000f)
                        + " delta=" + (Mathf.Floor(pilot.GetHeadingDeltaDegrees() * 10f) / 10f)
                        + " fwd=" + (Mathf.Floor(pilot.GetForwardInput() * 100f) / 100f)
                        + " switches=" + pilot.GetTargetSwitchCount()
                        + QueueFacts(m, pilot) + LatestReject(m) + TargetReject(m, pilot.GetTargetName());
                }
                // |v| is the motor's own velocity reading, not the pilot's intent.
                Vector3 v = m.GetPlanarVelocity();
                Debug.Log("[skirmish] t=" + (Mathf.Floor(elapsed * 10f) / 10f)
                    + " ball=" + i
                    + pilotPart
                    + " |v|=" + (Mathf.Floor(v.magnitude * 100f) / 100f)
                    + " d=" + (Mathf.Floor(m.GetSemanticDiameterWorldM() * 1000f) / 1000f)
                    + " absorbed=" + m.GetAbsorbedCount()
                    + " confirmed=" + m.GetConfirmedVolumeUnits()
                    + " banked=" + m.GetBankedScoreUnits()
                    + " events=" + m.GetAbsorbedEventCount()
                    + " rejects=" + m.GetRejectEventCount());
            }
        }

        /// <summary>True once every wired motor reports a started ball.</summary>
        private bool AllMotorsReady()
        {
            if (motors == null || motors.Length == 0) return false;
            for (int i = 0; i < motors.Length; i++)
            {
                KatamariBallMotor m = motors[i];
                if (m == null) continue;
                if (m.GetSemanticDiameterWorldM() <= 0f) return false;
            }
            return true;
        }

        /// <summary>
        /// Prints the queue's actual contents plus the gate's own distance and reach.
        /// </summary>
        /// <remarks>
        /// Facts, not verdicts: a boolean "is my target queued" is wrong forever when the
        /// comparison is wrong, which cost this project two rounds. d3d is the motor's
        /// stored 3-D distance; reach is recomputed here only for display, from the
        /// motor's published radii and the target's scale.
        /// </remarks>
        private string QueueFacts(KatamariBallMotor m, CpuBallPilot pilot)
        {
            int count = m.GetPendingCandidateCount();
            string text = " q=[";
            for (int i = 0; i < count; i++)
            {
                if (i > 0) text = text + ",";
                string id = m.GetPendingCandidateItemId(i);
                text = text + (id == null ? "?" : id);
                text = text + "@" + (Mathf.Floor(m.GetPendingCandidateAge(i) * 100f) / 100f);
            }
            text = text + "]";

            // The pilot's target may or may not be the queued item we just listed.
            int slot = -1;
            string targetName = pilot.GetTargetName();
            for (int i = 0; i < count; i++)
            {
                string id = m.GetPendingCandidateItemId(i);
                if (id != null && id == targetName) slot = i;
            }
            if (slot < 0) return text + " d3d=-1 reach=-1";

            UnityEngine.Transform target = pilot.GetTargetTransform();
            float size = target == null ? 0f : target.localScale.x;
            float itemRadius = Mathf.Sqrt(3f) * size * 0.5f;
            float reach = m.GetCollisionRadiusWorldM() + m.GetContactRadiusWorldM() + itemRadius;
            return text
                + " d3d=" + (Mathf.Floor(m.GetPendingCandidateDistance(slot) * 1000f) / 1000f)
                + " reach=" + (Mathf.Floor(reach * 1000f) / 1000f);
        }

        /// <summary>
        /// The most recent refusal on this motor, as a code plus a short name.
        /// </summary>
        /// <remarks>
        /// Without this a "d3d inside reach but nothing absorbed" reading still needs a
        /// guess: the refusal could be the kind whitelist, the growth cap, the size gate
        /// or a zero-value item (RejectNoGain), which is scene data, not geometry.
        /// </remarks>
        private string LatestReject(KatamariBallMotor m)
        {
            int count = m.GetRejectEventCount();
            if (count <= 0) return " rej=none";
            int last = count - 1;
            int reason = m.GetRejectEventReason(last);
            return " rej=" + reason + "(" + ReasonName(reason) + ")";
        }

        /// <summary>
        /// The latest refusal recorded for the pilot's CURRENT target, plus the value the
        /// motor holds for it.
        /// </summary>
        /// <remarks>
        /// The motor's newest refusal may belong to a different item, which is how a
        /// "noGain" reading appeared next to a prop whose own field said 1.0125. These two
        /// fields make the comparison per-item: rejTarget is that item's reason, pv is the
        /// value the motor actually received at submission.
        /// </remarks>
        private string TargetReject(KatamariBallMotor m, string targetName)
        {
            int count = m.GetRejectEventCount();
            string reasonText = "none";
            for (int i = count - 1; i >= 0; i--)
            {
                string id = m.GetRejectEventItemId(i);
                if (id == null) continue;
                if (id != targetName) continue;
                int reason = m.GetRejectEventReason(i);
                reasonText = reason + "(" + ReasonName(reason) + ")";
                break;
            }

            int slots = m.GetPendingCandidateCount();
            float value = -1f;
            for (int i = 0; i < slots; i++)
            {
                string id = m.GetPendingCandidateItemId(i);
                if (id != null && id == targetName) value = m.GetPendingCandidateValue(i);
            }
            return " rejTarget=" + reasonText + " pv=" + (Mathf.Floor(value * 1000f) / 1000f);
        }

        private string ReasonName(int reason)
        {
            if (reason == 1) return "notAbsorbable";
            if (reason == 2) return "kindNotAllowed";
            if (reason == 3) return "itemTooLarge";
            if (reason == 4) return "ballAtGrowthCap";
            if (reason == 5) return "outOfReach";
            if (reason == 6) return "invalidInput";
            if (reason == 7) return "noGain";
            return "?";
        }

        private void Setup()
        {
            if (motors == null || motors.Length == 0) return;

            // Pass the serialized array straight through. A string[] allocated here and
            // handed to another behaviour is the exact shape that produced a
            // __SetProgramVariable__ null reference in the single-ball lab.
            int kindCount = allowedKinds == null ? 0 : allowedKinds.Length;

            for (int i = 0; i < motors.Length; i++)
            {
                KatamariBallMotor m = motors[i];
                if (m == null) continue;
                m.SetAllowedKinds(allowedKinds, kindCount);
                m.AddPlane(groundNormal, groundOffset);
                m.TryBeginPlaying(Time.time);
            }

            if (obstacleBoxes != null)
            {
                for (int i = 0; i < obstacleBoxes.Length; i++)
                {
                    BoxCollider box = obstacleBoxes[i];
                    if (box == null) continue;
                    Bounds b = box.bounds;
                    for (int j = 0; j < motors.Length; j++)
                    {
                        KatamariBallMotor m = motors[j];
                        if (m != null) m.AddBox(b.min, b.max);
                    }
                }
            }

            elapsed = 0f;
            nextLogTime = 0f;
            initialised = true;
            Debug.Log("[skirmish] setup motors=" + motors.Length
                + " kinds=" + kindCount);
        }
    }
}
