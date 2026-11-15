using UdonSharp;
using UnityEngine;
using VRCKatamari.BallLayers;
using VRCKatamari.BallMotor;

namespace VRCKatamari.Ai
{
    /// <summary>
    /// Runs the CPU skirmish scene: one ledger and one motor per ball, four balls.
    /// </summary>
    /// <remarks>
    /// Each ball owns its own <see cref="KatamariBallMotor"/> on its own MotorHost
    /// object. That one-GameObject-per-motor rule is a project convention for clear
    /// ownership and a readable self-check, not an engine limit (see the assembly
    /// constraints in Docs/Design/BallMotor/README.md): sharing a GameObject with
    /// another UdonSharpBehaviour is legal, and the real hard constraint is the sync
    /// mode. This host only opens each round and reports what each
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

        [Header("Minimal round control (Lab only; not a game mode)")]
        // Real parameter: a round is 180 s. The scene may carry a smaller value purely
        // to accelerate a verification run; that is a Lab constant, never the default.
        [SerializeField] private float roundSeconds = 180f;
        [SerializeField] private float settlingSeconds = 3f;

        [Header("Conversion window (Lab)")]
        // Real parameter is 60 s. A scene may carry a smaller value purely to accelerate a
        // verification run; the log line says so explicitly so nobody reads 10 s as the rule.
        [SerializeField] private float rotationSeconds = 60f;

        private bool initialised;
        private int roundPhase;
        private float roundElapsed;
        private float windowElapsed;
        private int windowIndex;
        private bool windowOpen;
        private bool settlementDone;
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
            AdvanceRound();
            AdvanceWindow();

            if (elapsed < nextLogTime) return;
            nextLogTime = elapsed + logIntervalSeconds;

            float remaining = roundSeconds - elapsed;
            if (remaining < 0f) remaining = 0f;
            ProbeBank();

            Debug.Log("[round] t=" + (Mathf.Floor(remaining * 10f) / 10f)
                + " phase=" + (roundPhase == 0 ? "Playing" : (roundPhase == 1 ? "Settling" : "Ended")));

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
            float remaining = roundSeconds - elapsed;
            if (remaining < 0f) remaining = 0f;
            ProbeBank();

            Debug.Log("[round] t=" + (Mathf.Floor(remaining * 10f) / 10f)
                + " phase=" + (roundPhase == 0 ? "Playing" : (roundPhase == 1 ? "Settling" : "Ended")));

            for (int i = 0; i < motors.Length; i++)
            {
                KatamariBallMotor m = motors[i];
                if (m == null) continue;
                if (m.GetSemanticDiameterWorldM() <= 0f) return false;
            }
            return true;
        }

        /// <summary>
        /// Awards the three settlement bonuses by score rank, once per semantic key.
        /// </summary>
        /// <remarks>
        /// Ranking is read from each motor's banked score, the award is granted through
        /// TryAwardScore (the ledger's own rule), and nothing here keeps a bonus table: the
        /// ledger owns the score. Called twice on purpose - the second pass must be refused
        /// as duplicateKey on every entry.
        /// </remarks>
        private void SettleRewards()
        {
            if (motors == null) return;

            // Rank indices by banked score, descending. Four balls, so a simple selection.
            int[] order = new int[motors.Length];
            int count = 0;
            for (int i = 0; i < motors.Length; i++)
            {
                if (motors[i] == null) continue;
                order[count] = i;
                count = count + 1;
            }
            for (int a = 0; a < count; a++)
            {
                for (int b = a + 1; b < count; b++)
                {
                    if (motors[order[b]].GetBankedScoreUnits() > motors[order[a]].GetBankedScoreUnits())
                    {
                        int swap = order[a];
                        order[a] = order[b];
                        order[b] = swap;
                    }
                }
            }

            string[] keys = new string[3];
            keys[0] = "settle:first";
            keys[1] = "settle:second";
            keys[2] = "settle:third";

            for (int r = 0; r < count && r < 3; r++)
            {
                KatamariBallMotor m = motors[order[r]];
                int bankedBefore = m.GetBankedScoreUnits();
                bool ok = m.TryAwardScore(keys[r], 50);
                int total = m.GetBankedScoreUnits();
                if (ok)
                {
                    Debug.Log("[settle] ball=" + order[r] + " banked=" + bankedBefore
                        + " award=50 total=" + total + " rank=" + (r + 1)
                        + " awardedCount=" + m.GetAwardedAwardCount());
                }
                else
                {
                    Debug.Log("[settle] duplicate key=" + keys[r] + " ball=" + order[r]
                        + " failure=" + m.GetLastAwardFailure()
                        + " total=" + total + " (unchanged)");
                }
            }
        }

        /// <summary>
        /// Rotates the conversion window and prints every transition.
        /// </summary>
        /// <remarks>
        /// The window is a Lab/round concept, not a motor one: TryBankWithReason only answers
        /// "can this ball convert", so the host keeps its own window state and prints it
        /// beside the motor's reason instead of folding the two into one number.
        /// </remarks>
        private void AdvanceWindow()
        {
            windowElapsed = windowElapsed + Time.deltaTime;
            if (windowElapsed < rotationSeconds) return;
            windowElapsed = 0f;
            windowIndex = windowIndex + 1;
            windowOpen = !windowOpen;
            Debug.Log("[rotate] window " + (windowOpen ? "closed -> open" : "open -> closed")
                + " at t=" + (Mathf.Floor(elapsed * 10f) / 10f)
                + " index=" + windowIndex
                + " (lab acceleration=" + rotationSeconds + "s; real 60 s)");
        }

        /// <summary>
        /// One conversion attempt per ball per second while the window is open, with the
        /// numbers needed to recompute the expected shrink independently.
        /// </summary>
        private void ProbeBank()
        {
            if (roundPhase != 0) return;
            if (!windowOpen) { Debug.Log("[bank] window=closed skipped"); return; }

            for (int i = 0; i < motors.Length; i++)
            {
                KatamariBallMotor m = motors[i];
                if (m == null) continue;
                float before = m.GetSemanticDiameterWorldM();
                float volume = m.GetConfirmedVolumeUnits();
                int residualPercent = 25;
                float residual = 0.25f;

                // Expected shrink, computed from the same public math the motor uses.
                float startCore, startCollision, startDiameter, startEffective;
                float expected = -1f;
                bool sized = SizeModelMath.TryDerive(0f, 3.4390244f, 0.0041f, 1.1f, 1f, 1f,
                    out startCore, out startCollision, out startDiameter, out startEffective);
                if (sized && BankShrinkMath.TryPostBankDiameter(startDiameter, before, residual, out expected))
                {
                    // expected is the rule's answer; kept only for the log line
                }

                int reason;
                bool ok = m.TryBankWithReason(Time.time, "lab-bank-" + i + "-" + (int)(elapsed * 10f), out reason);
                float after = m.GetSemanticDiameterWorldM();
                Debug.Log("[bank] ball=" + i
                    + " before=" + (Mathf.Floor(before * 10000f) / 10000f)
                    + " after=" + (Mathf.Floor(after * 10000f) / 10000f)
                    + " expected=" + (expected < 0f ? "-" : (Mathf.Floor(expected * 10000f) / 10000f).ToString())
                    + " volume=" + (Mathf.Floor(volume * 100f) / 100f)
                    + " residual=" + residual
                    + " reason=" + reason + (ok ? "(ok)" : ""));
            }
        }

        /// <summary>
        /// The smallest round state machine this Lab needs: Playing -> Settling -> Ended.
        /// </summary>
        /// <remarks>
        /// Deliberately here and not in Runtime: the real game mode is a later task, and a
        /// second round implementation in Runtime would be the kind of duplicate this
        /// project keeps paying for. It exists only so the conversion and settlement rules
        /// can be exercised at all.
        /// </remarks>
        private void AdvanceRound()
        {
            roundElapsed = roundElapsed + Time.deltaTime;
            if (roundPhase == 0)
            {
                if (roundElapsed >= roundSeconds)
                {
                    roundPhase = 1;
                    roundElapsed = 0f;
                    Debug.Log("[round] phase=Settling after " + roundSeconds + "s");
                    SettleRewards();
                    // Deliberately run the settlement a second time, exactly as a reconnect
                    // or event replay would. Every award must come back duplicateKey and no
                    // score may move; that is what makes "once" structural.
                    SettleRewards();
                }
            }
            else if (roundPhase == 1)
            {
                if (roundElapsed >= settlingSeconds)
                {
                    roundPhase = 2;
                    roundElapsed = 0f;
                    Debug.Log("[round] phase=Ended");
                }
            }
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

            float remaining = roundSeconds - elapsed;
            if (remaining < 0f) remaining = 0f;
            ProbeBank();

            Debug.Log("[round] t=" + (Mathf.Floor(remaining * 10f) / 10f)
                + " phase=" + (roundPhase == 0 ? "Playing" : (roundPhase == 1 ? "Settling" : "Ended")));

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
