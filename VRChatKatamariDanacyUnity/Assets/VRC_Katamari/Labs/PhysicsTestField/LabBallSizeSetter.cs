using UdonSharp;
using UnityEngine;
using VRCKatamari.BallMotor;

namespace VRCKatamari.Labs.PhysicsTestField
{
    /// <summary>
    /// Sets a running ball to a target diameter through the existing pickup seam: feed whole
    /// volume units with <see cref="KatamariBallMotor.SubmitCandidate"/>, read the diameter back,
    /// and use the analytic size-model slope to compute the next feed. No runtime code changes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The diameter is a pure function of the confirmed volume (<c>SizeModelMath.TryDerive</c>), so
    /// the only thing a size setter can own is "how many volume units". Feeding is done by the
    /// motor's own pickup stage, which is why nothing in <c>Runtime/</c> changes.
    /// </para>
    /// <para>
    /// <b>The slope is analytic, not measured and not hard-coded.</b> The core radius is
    /// <c>cbrt(3 * cubicMetersPerVolumeUnit * (baseline + confirmed) / 4pi)</c> and the semantic
    /// diameter is proportional to the core radius, so <c>D^3</c> is exactly linear in the TOTAL
    /// volume units. That makes <c>slope = d(D^3)/d(unit) = D^3 / (baseline + confirmed)</c> at the
    /// current size, so the feeds are exact: <c>units = (target^3 - D^3) / slope</c>. Both
    /// <c>cubicMetersPerVolumeUnit</c> and the semantic/core ratio cancel out of that expression,
    /// which is why the formula needs exactly one motor number - <c>baselineVolumeUnits</c> - and
    /// not the pricing constant. That number has no getter, so the criteria importer mirrors it
    /// from the scene motor into <see cref="sizeModelBaselineVolumeUnits"/>.
    /// </para>
    /// <para>
    /// <b>Why not a measured slope (the previous design).</b> Measuring <c>d(D^3)/dV</c> needs two
    /// settled samples with different volume, i.e. it needs at least one feed. At the 0.30 tier the
    /// ball is already at the target after the round reset, so <see cref="Feed"/> converged on the
    /// first frame, never fed, and the measured slope - and therefore the quantum the criterion
    /// divides by - stayed zero. The quantum is a property of the size model, not of a run, so it
    /// is derived, and there is exactly one derivation.
    /// </para>
    /// <para>
    /// <b>One request per run, written by one caller.</b> The driver resets the round between runs
    /// and the reset re-prices the ball to the baseline, so an unchanged target still needs a fresh
    /// pass: <see cref="SetDiameterM"/> is the setter's only request entry point and it always
    /// starts a new request. The driver calls it exactly once per run (in <c>Switch</c>) and only
    /// reads <see cref="IsConverged"/> / <see cref="HasFailed"/> afterwards. Two earlier designs
    /// failed here: a "same target" short-circuit kept the previous run's converged state (pass-1
    /// matrix: only the first cell per tier was resized), and re-issuing the request from the poll
    /// loop cleared <c>converged</c> once per frame (pass-2: the size stage timed out on a ball
    /// that was already at the target).
    /// </para>
    /// <para>
    /// <b>The resolution floor is one volume unit, and it is a real limit.</b> One unit is
    /// <c>0.0041 m^3</c>, so the diameter grid is ~29 mm at D = 0.30, ~7.3 mm at 0.60, ~2.2 mm at
    /// 1.10 and ~0.54 mm at 2.20. "Within 2 mm" is therefore NOT reachable at the small presets -
    /// the loop stops when the next whole unit would move the diameter further away, and reports
    /// the error in quanta (<see cref="GetLastErrorM"/> / <see cref="GetQuantumM"/>) so the
    /// criterion can be honest.
    /// </para>
    /// <para>
    /// <b>If the size model was not baked</b> (the importer had no motor link to read), the quantum
    /// reads 0 and a request beyond the current size fails with <c>noSizeModel</c> instead of
    /// crawling one unit at a time: the driver then reports the size stage as blocked and
    /// <c>size_accuracy</c> as unavailable-with-a-reason, never as a pass.
    /// </para>
    /// <para>
    /// <b>One candidate in flight.</b> The candidate queue holds four entries and a fifth submit
    /// latches the motor's permanent overflow counter, which the field asserts is zero. Feeding
    /// only while <c>GetPendingCandidateCount() == 0</c> keeps the queue at one.
    /// </para>
    /// <para>
    /// Whole units are not a style choice: <c>PickupAbsorbMath.TryAbsorbValueToLedgerGain</c>
    /// refuses a fractional value, so a 0.35-unit feed is dropped as "no gain" and the loop would
    /// spin forever.
    /// </para>
    /// <para>
    /// Deliberately absent: a reset. "Back to 0.30" is
    /// <c>motor.TryResetForNewRound(id, cell start, heading, out reason)</c>, which needs the
    /// cell's start pose - that belongs to <see cref="PhysicsTestFieldDriver"/>, and a setter that
    /// also teleports the ball would own two unrelated things.
    /// </para>
    /// <para>
    /// Known side effects (design section 8.3): the absorb count, the confirmed volume and the
    /// absorb volume limit all grow. Every field check that reads them uses a baseline delta.
    /// </para>
    /// </remarks>
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class LabBallSizeSetter : UdonSharpBehaviour
    {
        [Header("Links")]
        [SerializeField] private KatamariBallMotor motor;

        [Header("Presets (filled from physics_test_field.json diameters[])")]
        [SerializeField] private float[] presetDiametersM;
        [SerializeField] private string[] presetLabels;

        [Header("Size model (filled by the criteria importer from the scene motor)")]
        [Tooltip("KatamariBallMotor.baselineVolumeUnits, in volume units. The analytic quantum " +
                 "needs this one number: D^3 is linear in (baseline + confirmed) and the pricing " +
                 "constant cancels. 0 = not baked, so the quantum reads 0 and the driver reports " +
                 "the metric as unavailable instead of guessing.")]
        [SerializeField] private float sizeModelBaselineVolumeUnits;

        [Header("Feeder candidate")]
        [Tooltip("Kind the motor's whitelist must contain, or the absorb gate refuses the feed.")]
        [SerializeField] private string feederKind = "lab-feeder";
        [Tooltip("The candidate's declared bounds. Gameplay volume only; it is never a real object.")]
        [SerializeField] private Vector3 feederBounds = new Vector3(0.02f, 0.02f, 0.02f);
        [Tooltip("Stop when the error is this small - unless one volume unit is coarser than this.")]
        [SerializeField] private float toleranceM = 0.002f;
        [SerializeField] private int maxAbsorbsPerRequest = 6;
        [Tooltip("Seconds between feeds. The motor steps at 50 Hz, so a shorter gap only queues.")]
        [SerializeField] private float feedIntervalSeconds = 0.05f;

        // ---- request state ----
        private float targetM;
        private bool requestActive;
        private bool converged;
        private int absorbsThisRequest;
        private int absorbCount;
        private int generationSequence;
        private float nextFeedTime;
        private string lastFailure = "";

        // ---- analytic size model ----
        private float lastErrorM;

        // Stamped into the first log line. A whole 4 x 9 matrix was invalid because the scene ran
        // the previous U# program while its data came from the new importer; this makes "which
        // program am I running" a one-line check instead of a forensic exercise.
        private const string SetterRevision = "2026-11-15c";

        public void Start()
        {
            // baselineVolumeUnits is the one input the setter cannot read from a getter, so print
            // the baked value: 0 here means the criteria importer did not bake it.
            Debug.Log("[ptf] setter build=" + SetterRevision
                + " baselineVolumeUnits=" + sizeModelBaselineVolumeUnits);
        }

        public void Update()
        {
            if (!requestActive) return;
            if (motor == null) return;
            Feed();
        }

        // ---- operator API ------------------------------------------------------

        /// <summary>
        /// Starts one sizing pass for one target diameter. This is the setter's ONLY request entry
        /// point: it resets every per-request field and then feeds until the ball reaches the
        /// target. The driver calls it exactly once per run (from Switch, after the round reset);
        /// a caller that wants the result polls <see cref="IsConverged"/> / <see cref="HasFailed"/>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Do not call this every frame.</b> Each call is a new request and clears
        /// <c>converged</c>; re-issuing it from a poll loop made the flag flip false once per frame,
        /// so the driver never observed convergence and the size stage timed out (the 2026-11-15
        /// pass-2 defect). The polling contract is read-only: <see cref="IsConverged"/>,
        /// <see cref="HasFailed"/>, <see cref="GetQuantumM"/>, <see cref="GetLastErrorM"/>.
        /// </para>
        /// <para>
        /// It also has to reset on every run rather than short-circuit on "same target": the round
        /// reset re-prices the ball back to the baseline, so the second cell in a tier must re-feed
        /// even though the target diameter did not change (the 2026-11-15 pass-1 defect, where only
        /// the first cell per tier was really resized).
        /// </para>
        /// </remarks>
        public void SetDiameterM(float targetMeters)
        {
            if (targetMeters <= 0f) return;
            targetM = targetMeters;
            requestActive = true;
            converged = false;
            absorbsThisRequest = 0;
            absorbCount = 0;
            lastFailure = "";
            lastErrorM = 0f;
            nextFeedTime = 0f;
        }

        /// <summary>HUD button target: 0..presetDiametersM.Length-1.</summary>
        public void SelectPreset(int index)
        {
            if (presetDiametersM == null) return;
            if (index < 0 || index >= presetDiametersM.Length) return;
            SetDiameterM(presetDiametersM[index]);
        }

        /// <summary>
        /// HUD button target by label ("0.30"). Compares text, so no float is parsed in Udon.
        /// </summary>
        /// <remarks>
        /// The comparison is exact on purpose: <c>string.Trim()</c> is not part of the string
        /// externs this project's Udon code has proven, and a button that passes " 0.30" is a
        /// wiring mistake worth seeing rather than papering over.
        /// </remarks>
        public void SetDiameterFromLabel(string label)
        {
            if (presetLabels == null || presetDiametersM == null) return;
            if (label == null) return;
            for (int i = 0; i < presetLabels.Length && i < presetDiametersM.Length; i++)
            {
                if (presetLabels[i] == null) continue;
                if (string.CompareOrdinal(presetLabels[i], label) == 0)
                {
                    SetDiameterM(presetDiametersM[i]);
                    return;
                }
            }
        }

        /// <summary>Stops feeding; the ball keeps whatever size it reached.</summary>
        public void Cancel()
        {
            requestActive = false;
        }

        // ---- status ------------------------------------------------------------

        public bool IsConverged()
        {
            return converged;
        }

        public bool IsRequestActive()
        {
            return requestActive;
        }

        public bool HasFailed()
        {
            return lastFailure != null && lastFailure.Length > 0;
        }

        public string GetLastFailure()
        {
            if (lastFailure == null) return "";
            return lastFailure;
        }

        public float GetTargetDiameterM()
        {
            return targetM;
        }

        public int GetAbsorbCount()
        {
            return absorbCount;
        }

        /// <summary>Signed error of the last measurement: measured - target, metres.</summary>
        public float GetLastErrorM()
        {
            return lastErrorM;
        }

        /// <summary>
        /// One volume unit expressed as a diameter change at the current size, metres.
        /// Computed from the size model on demand, so it exists before the first feed of the first
        /// run and can never be a stale copy from an earlier request. Zero when the model was not
        /// baked or the motor is not ready.
        /// </summary>
        public float GetQuantumM()
        {
            float slope;
            float quantum;
            ReadSizeModel(out slope, out quantum);
            return quantum;
        }

        /// <summary>d(D^3)/d(volume unit) at the current size; 0 when the model was not baked.</summary>
        public float GetCubedSlope()
        {
            float slope;
            float quantum;
            ReadSizeModel(out slope, out quantum);
            return slope;
        }

        public bool IsCalibrated()
        {
            return sizeModelBaselineVolumeUnits > 0f && motor != null;
        }

        /// <summary>
        /// The two size-model readings at the CURRENT ball size, derived from one expression.
        /// </summary>
        /// <remarks>
        /// The core radius is <c>cbrt(3 * cubic * total / 4pi)</c> and the semantic diameter is
        /// proportional to it, so <c>D^3</c> is linear in the total volume units: the slope is
        /// <c>D^3 / total</c> and one unit is <c>D / (3 * total)</c>. Computed on every read, so a
        /// cached quantum from the previous request can never leak into a new run.
        /// </remarks>
        private void ReadSizeModel(out float slopeCubedPerUnit, out float quantumM)
        {
            slopeCubedPerUnit = 0f;
            quantumM = 0f;
            if (motor == null) return;
            if (sizeModelBaselineVolumeUnits <= 0f) return;

            float diameter = motor.GetSemanticDiameterWorldM();
            if (diameter <= 0f) return;
            float totalUnits = sizeModelBaselineVolumeUnits + motor.GetConfirmedVolumeUnits();
            if (totalUnits <= 0f) return;

            float cubed = diameter * diameter * diameter;
            if (cubed <= 0f) return;
            slopeCubedPerUnit = cubed / totalUnits;
            quantumM = diameter / (3f * totalUnits);
        }

        // ---- the loop ----------------------------------------------------------

        private void Feed()
        {
            if (converged) return;
            if (motor.GetSemanticDiameterWorldM() <= 0f) return;

            // One candidate in flight: a full queue would latch the motor's overflow counter,
            // and the field asserts that counter is 0.
            if (motor.GetPendingCandidateCount() > 0) return;

            float diameter = motor.GetSemanticDiameterWorldM();
            float cubed = diameter * diameter * diameter;

            // The same one derivation the getters use, read fresh every frame: no cached slope and
            // no cached quantum, so a new run (whose round reset moved the ball back to the
            // baseline) can never inherit the previous run's numbers.
            float slopeCubedPerUnit;
            float quantum;
            ReadSizeModel(out slopeCubedPerUnit, out quantum);

            float error = diameter - targetM;
            lastErrorM = error;
            if (error < 0f) error = -error;

            float allowed = toleranceM;
            if (quantum > allowed) allowed = quantum;
            if (error <= allowed)
            {
                converged = true;
                lastFailure = "";
                return;
            }

            if (diameter > targetM + allowed)
            {
                // Growth only. A driver that wants a smaller ball resets the round, which
                // re-prices from the baseline volume.
                Fail("cannotShrink");
                return;
            }

            if (slopeCubedPerUnit <= 0f)
            {
                // No size model: a one-unit-at-a-time crawl would spend the whole absorb budget
                // and still miss the target, so name the missing input instead of trying.
                Fail("noSizeModel");
                return;
            }

            if (absorbsThisRequest >= maxAbsorbsPerRequest)
            {
                Fail("capExceeded");
                return;
            }

            if (Time.time < nextFeedTime) return;

            float targetCubed = targetM * targetM * targetM;
            float remaining = targetCubed - cubed;
            float wanted = remaining / slopeCubedPerUnit;
            int units = (int)(wanted + 0.5f);
            if (units < 1)
            {
                // The next whole unit lands further from the target than the current size:
                // this is the resolution floor, not a failure.
                converged = true;
                lastFailure = "";
                return;
            }

            // Refuse a feed that would move away from the target even though it is "one
            // unit": cbrt is compared in the cube domain to avoid a root.
            float afterCubed = cubed + (float)units * slopeCubedPerUnit;
            float beforeGap = targetCubed - cubed;
            float afterGap = targetCubed - afterCubed;
            if (beforeGap < 0f) beforeGap = -beforeGap;
            if (afterGap < 0f) afterGap = -afterGap;
            if (afterGap >= beforeGap)
            {
                converged = true;
                lastFailure = "";
                return;
            }

            generationSequence = generationSequence + 1;
            string itemId = "ptf-feeder-" + generationSequence.ToString();
            bool queued = motor.SubmitCandidate(true, feederKind, feederBounds,
                motor.GetCenterWorld(), (float)units, itemId, generationSequence);
            if (!queued)
            {
                Fail("submitRefused");
                return;
            }

            absorbsThisRequest = absorbsThisRequest + 1;
            absorbCount = absorbCount + 1;
            nextFeedTime = Time.time + feedIntervalSeconds;
        }

        private void Fail(string reason)
        {
            lastFailure = reason;
            requestActive = false;
            Debug.LogWarning("[ptf] size failed='" + reason + "' target=" + targetM
                + " diameter=" + motor.GetSemanticDiameterWorldM()
                + " absorbs=" + absorbCount);
        }
    }
}
