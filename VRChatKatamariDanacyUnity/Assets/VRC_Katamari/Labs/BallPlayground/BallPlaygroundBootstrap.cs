using UdonSharp;
using UnityEngine;
using UnityEngine.UI;
using VRCKatamari.BallMotor;

namespace VRCKatamari.Labs.BallPlayground
{
    /// <summary>
    /// Drives one ball through a scripted timeline in the playground scene and
    /// reports the run on screen and in the console.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Why a scripted timeline instead of player input: this slice exists to prove
    /// that the motor composes and that its numbers move, not to judge feel. A fixed
    /// timeline makes two runs comparable, so a changed end diameter means a changed
    /// rule rather than a different set of key presses. The input seam it drives is
    /// the one the motor already exposes (<see cref="KatamariBallMotor.SetMoveAxes"/>),
    /// so nothing about the motor changes to make this run.
    /// </para>
    /// <para>
    /// The bootstrap owns scene registration and presentation only: it registers the
    /// ground plane and the boxes, opens the round, scales the visual ball to the
    /// semantic diameter the motor reports, and prints a one-line sample every
    /// second so a run leaves evidence in the console as well as on screen.
    /// </para>
    /// </remarks>
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class BallPlaygroundBootstrap : UdonSharpBehaviour
    {
        [SerializeField] private KatamariBallMotor motor;
        [SerializeField] private Transform ballVisual;
        [SerializeField] private Text hudText;
        [SerializeField] private BoxCollider[] obstacleBoxes;
        [SerializeField] private string[] allowedKinds;
        [SerializeField] private Transform probeProp;
        [SerializeField] private int propTotal = 24;

        [Header("Presentation")]
        [SerializeField] private Transform cameraFollow;
        [SerializeField] private Vector3 cameraOffset = new Vector3(0f, 7f, -9f);
        [SerializeField] private Vector3 cameraEuler = new Vector3(35f, 0f, 0f);

        [Header("Ground")]
        [SerializeField] private Vector3 groundNormal = new Vector3(0f, 1f, 0f);
        [SerializeField] private float groundOffset = 0f;

        [Header("Scripted timeline (seconds)")]
        [SerializeField] private float runSeconds = 20f;
        [SerializeField] private float logIntervalSeconds = 1f;

        private float elapsed;
        private float nextLogTime;
        private float startDiameter;
        private bool initialised;
        private int boxesRegistered;
        private int propsQueuedAtStart;
        private bool finished;
        private bool playing;
        private bool selfCheckFailed;
        private bool starterCheckDone;
        private int lastTraceCount;
        private int starterWaitFrames;

        public void Start()
        {
            // Startup self-check. A wrong wiring used to be invisible for a whole run:
            // the motor that ran Start was a different instance from the one every
            // caller held, so AddPlane threw on unallocated stores, GetSemanticDiameter
            // returned 0, and the run looked like "a small ball that absorbs nothing".
            // Fail loudly on frame one instead.
            //
            // Udon has no FindObjectsOfType, so the scene-instance-count check cannot
            // live here. The three conditions below are its Udon-side equivalent: a
            // null motor, a motor sharing this GameObject (the exact shape that makes
            // UdonSharp disable it), and a motor whose Start never ran. The editor-side
            // audit in the lab build step asserts the instance count is exactly 1
            // before a run is recorded.
            selfCheckFailed = false;

            if (motor == null)
            {
                Debug.LogError("[BallPlayground] self-check FAILED: motor reference is null; nothing can drive the ball.");
                selfCheckFailed = true;
                return;
            }

            if (motor.transform == transform)
            {
                Debug.LogError("[BallPlayground] self-check FAILED: the motor shares this GameObject with another UdonSharpBehaviour. A motor must own its own MotorHost object (Docs/Design/BallMotor/README.md, assembly constraints); sharing disables its UdonBehaviour and it never ticks.");
                selfCheckFailed = true;
                return;
            }

            // The diameter check cannot live here: Unity does not promise that the
            // motor's Start runs before this one, and in ClientSim it does not, so a
            // zero read at this point says nothing. It runs on the first Update
            // instead (all Starts are over by then).
        }

        public void Update()
        {
            if (selfCheckFailed) return;

            if (!starterCheckDone)
            {
                // Udon may dispatch this behaviour's first Update before the motor's
                // Start, so a zero diameter on frame one is normal, not a fault. Wait
                // for the motor to come up, and only treat a persistent zero as the
                // wrong-instance failure it is.
                float readyDiameter = motor.GetSemanticDiameterWorldM();
                if (readyDiameter > 0f)
                {
                    starterCheckDone = true;
                }
                else
                {
                    starterWaitFrames = starterWaitFrames + 1;
                    if (starterWaitFrames < 120) return;
                }
                if (!starterCheckDone)
                {
                    // Separate message from "the ball is small" on purpose: zero here
                    // means this motor's Start never ran (stores still null), i.e. the
                    // caller holds the wrong instance, not that the ball is tiny.
                    Debug.LogError("[BallPlayground] self-check FAILED: the motor reports diameter 0. That is not a small ball - it means THIS instance's Start() never ran (its arrays are unallocated, so AddPlane and the pickup calls will throw). The caller is holding the wrong motor instance.");
                    selfCheckFailed = true;
                    return;
                }
            }

            if (!initialised)
            {
                if (finished) return;
                Setup();
                if (!initialised) return;
            }

            if (!playing)
            {
                playing = motor.TryBeginPlaying(Time.time);
                if (playing) Debug.Log("[BallPlayground] phase Playing after retry at t=" + Time.time);
            }

            elapsed = elapsed + Time.deltaTime;
            DriveTimeline();

            float diameter = motor.GetSemanticDiameterWorldM();
            if (ballVisual != null)
            {
                ballVisual.localScale = new Vector3(diameter, diameter, diameter);
            }

            // Presentation only: the camera trails the ball so a screenshot after
            // ten seconds still frames it. Fixed angle, offset scales with the ball
            // so a grown ball is not clipped by the near plane.
            if (cameraFollow != null && ballVisual != null)
            {
                Vector3 offset = cameraOffset;
                float pullBack = diameter * 1.5f;
                offset.z = offset.z - pullBack;
                offset.y = offset.y + pullBack * 0.5f;
                cameraFollow.position = ballVisual.position + offset;
                cameraFollow.rotation = Quaternion.Euler(cameraEuler);
            }

            if (elapsed >= nextLogTime)
            {
                nextLogTime = elapsed + logIntervalSeconds;
                LogSample(diameter);
                LogTraceIfNew();
            }

            UpdateHud(diameter);

            if (elapsed >= runSeconds)
            {
                finished = true;
                Debug.Log("[BallPlayground] run finished t=" + elapsed
                    + " endDiameter=" + diameter
                    + " absorbed=" + motor.GetAbsorbedCount()
                    + " overflow=" + motor.GetOverflowCount()
                    + " stuck=" + motor.GetStuckSeconds());
                initialised = false;
                motor.SetMoveAxes(0f, 0f, 0f);
            }
        }

        /// <summary>
        /// Registers scene data with the motor and opens the round. Runs on the first
        /// Update, when the motor's own Start has certainly allocated its stores.
        /// </summary>
        private void Setup()
        {
            // The whitelist is a serialized array (set on the prefab) rather than a
            // string[] allocated here: a runtime-allocated array handed across the
            // behaviour boundary was the one call that produced a per-frame
            // __SetProgramVariable__ null reference.
            int kindCount = 0;
            if (allowedKinds != null) kindCount = allowedKinds.Length;
            motor.SetAllowedKinds(allowedKinds, kindCount);
            // Read back instead of assuming: allowedKindCount == 0 means "deny every
            // kind", so a whitelist that silently failed would refuse every prop and
            // the run would look empty with no error anywhere.
            int kindsApplied = motor.GetAllowedKindCount();
            Debug.Log("[BallPlayground] allowedKindCount=" + kindsApplied + " (0 = deny all)");
            if (kindsApplied <= 0)
            {
                Debug.LogError("[BallPlayground] kind whitelist did not apply; every candidate will be refused");
            }

            if (probeProp != null)
            {
                // One-shot coordinate probe: if this does not read (0, 0.05, 1.5) then
                // the candidate positions and the ball centre are in different
                // coordinate spaces and every distance is wrong for that reason alone.
                Debug.Log("[BallPlayground] probe prop pos=" + probeProp.position
                    + " localPos=" + probeProp.localPosition
                    + " parent=" + (probeProp.parent == null ? "null" : probeProp.parent.name)
                    + " rootScale=" + probeProp.root.localScale);
            }
            else
            {
                Debug.LogError("[BallPlayground] probe prop is not wired; coordinate check unavailable");
            }

            motor.AddPlane(groundNormal, groundOffset);

            boxesRegistered = 0;
            if (obstacleBoxes != null)
            {
                for (int i = 0; i < obstacleBoxes.Length; i++)
                {
                    BoxCollider box = obstacleBoxes[i];
                    if (box == null) continue;
                    Bounds bounds = box.bounds;
                    if (motor.AddBox(bounds.min, bounds.max)) boxesRegistered = boxesRegistered + 1;
                }
            }

            // A phase transition carries a timestamp; 0 can be refused as a
            // zero-length countdown, which would leave the ledger out of Playing
            // and make every absorb commit fail (the symptom: the four candidate
            // slots never drain). Retry on wall time until the motor accepts.
            playing = motor.TryBeginPlaying(Time.time);
            Debug.Log("[BallPlayground] beginPlaying=" + playing + " at t=" + Time.time);
            startDiameter = motor.GetSemanticDiameterWorldM();
            elapsed = 0f;
            nextLogTime = 0f;
            initialised = true;

            Debug.Log("[BallPlayground] start diameter=" + startDiameter
                + " boxes=" + boxesRegistered + " props=" + propTotal
                + " bank rule stays unexercised in this slice");
            UpdateHud(startDiameter);
        }

        /// <summary>
        /// The fixed timeline. Every stage is forward motion, so the run always
        /// sweeps the same band of the floor; the turns make it pass the rows of
        /// props a second time once it has grown.
        /// </summary>
        private void DriveTimeline()
        {
            // Straight by decision, not by accident: this slice exists to prove
            // rolling -> absorbing -> growing, and steering has its own task. An
            // earlier revision turned at 8-10 s, which put the ball on a 27.6 deg
            // chord and made every prop miss for reasons that looked like a pickup
            // bug. Turns return when the slice has a feel goal.
            motor.SetMoveAxes(1f, 0f, 0f);
        }

        private void LogSample(float diameter)
        {
            Debug.Log("[BallPlayground] t=" + Mathf.Floor(elapsed * 10f) / 10f
                + " d=" + Mathf.Floor(diameter * 1000f) / 1000f
                + " absorbed=" + motor.GetAbsorbedCount()
                + " queued=" + motor.GetPendingCandidateCount()
                + " overflow=" + motor.GetOverflowCount()
                + " events=" + motor.GetAbsorbedEventCount()
                + " dropped=" + motor.GetAbsorbedEventDroppedCount()
                + " rejects=" + motor.GetRejectEventCount()
                + " center=" + motor.GetCenterWorld()
                + " " + QueueSnapshotText()
                + " " + LatestRejectText());
        }

        private void UpdateHud(float diameter)
        {
            if (hudText == null) return;
            hudText.text = "diameter " + (Mathf.Floor(diameter * 1000f) / 1000f) + " m\n"
                + "absorbed " + motor.GetAbsorbedCount() + " / " + propTotal + "\n"
                + "queued " + motor.GetPendingCandidateCount() + "\n"
                + "overflow " + motor.GetOverflowCount() + "\n"
                + "events " + motor.GetAbsorbedEventCount() + "  rejects " + motor.GetRejectEventCount() + "\n"
                + LatestRejectText() + "\n"
                + "t " + (Mathf.Floor(elapsed * 10f) / 10f) + " s";
        }



        /// <summary>
        /// Names the queued candidates and their distances, formatted here rather than
        /// in the motor: the motor exposes data, presentation stays in the host.
        /// </summary>
        private string QueueSnapshotText()
        {
            int count = motor.GetPendingCandidateCount();
            string text = "queue=" + count + " ids=";
            for (int i = 0; i < count; i++)
            {
                if (i > 0) text = text + ",";
                text = text + motor.GetPendingCandidateItemId(i);
            }
            text = text + " d=";
            for (int i = 0; i < count; i++)
            {
                if (i > 0) text = text + ",";
                text = text + (Mathf.Floor(motor.GetPendingCandidateDistance(i) * 100f) / 100f);
            }
            return text;
        }

        /// <summary>
        /// Prints the evaluation trace for the item the motor is watching, once per new
        /// evaluation batch. Silent when the motor has no debug item id set.
        /// </summary>
        private void LogTraceIfNew()
        {
            int count = motor.GetTraceCount();
            if (count == lastTraceCount) return;
            lastTraceCount = count;
            Debug.Log("[trace] " + motor.GetDebugTraceItemId()
                + " n=" + count
                + " ballZ=" + (Mathf.Floor(motor.GetTraceLastBallZ() * 1000f) / 1000f)
                + " d=" + (Mathf.Floor(motor.GetTraceLastDistance() * 1000f) / 1000f)
                + " outcome=" + motor.GetTraceLastOutcome()
                + " reach=" + (Mathf.Floor(motor.GetTraceLastReach() * 1000f) / 1000f));
        }

        /// <summary>
        /// The most recent refused candidate and why, as one short line.
        /// </summary>
        /// <remarks>
        /// Reported once per second (and on the HUD) rather than per frame and per
        /// candidate: the point is that a first run says WHICH branch refused, without
        /// a 24-line spam. -1 means nothing has been refused yet.
        /// </remarks>
        private string LatestRejectText()
        {
            int count = motor.GetRejectEventCount();
            if (count <= 0) return "reject none";
            int last = count - 1;
            return "reject " + motor.GetRejectEventItemId(last)
                + " " + ReasonName(motor.GetRejectEventReason(last))
                + " d=" + (Mathf.Floor(motor.GetRejectEventDistance(last) * 1000f) / 1000f);
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
            return "reason" + reason;
        }

        // ---- Read-only seams so the run can be sampled from outside ----

        public float GetStartDiameter()
        {
            return startDiameter;
        }

        public float GetElapsed()
        {
            return elapsed;
        }

        public int GetBoxesRegistered()
        {
            return boxesRegistered;
        }
    }
}
