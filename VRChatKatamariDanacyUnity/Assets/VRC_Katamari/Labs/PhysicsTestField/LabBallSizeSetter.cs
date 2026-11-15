using UdonSharp;
using UnityEngine;
using VRCKatamari.BallMotor;

namespace VRCKatamari.Labs.PhysicsTestField
{
    /// <summary>
    /// Sets a running ball to a target diameter through the existing pickup seam: feed whole
    /// volume units with <see cref="KatamariBallMotor.SubmitCandidate"/>, read the diameter back,
    /// and use the measured slope to compute the next feed. No runtime code changes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The diameter is a pure function of the confirmed volume (<c>SizeModelMath.TryDerive</c>), so
    /// the only thing a size setter can own is "how many volume units". Feeding is done by the
    /// motor's own pickup stage, which is why nothing in <c>Runtime/</c> changes.
    /// </para>
    /// <para>
    /// <b>The slope is measured, not hard-coded.</b> <c>D^3</c> is linear in the confirmed volume
    /// units (<c>cubicMetersPerVolumeUnit</c> and <c>baselineVolumeUnits</c> are private, and a
    /// hard-coded 0.0041 would go stale the day the pricing changes), so one probe feed gives
    /// <c>slope = d(D^3)/d(units)</c> and the next feeds are exact:
    /// <c>units = (target^3 - D^3) / slope</c>.
    /// </para>
    /// <para>
    /// <b>The resolution floor is one volume unit, and it is a real limit.</b> One unit is
    /// <c>0.0041 m^3</c>, so the diameter grid is ~7 mm at D = 0.60 and ~0.5 mm at D = 2.20.
    /// "Within 2 mm" is therefore NOT reachable at the small presets - the loop stops when the
    /// next whole unit would move the diameter further away, and reports the error in quanta
    /// (<see cref="GetLastErrorM"/> / <see cref="GetQuantumM"/>) so the criterion can be honest.
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

        // ---- measured slope ----
        private bool hasSample;
        private float lastVolumeUnits;
        private float lastCubedDiameter;
        private float slopeCubedPerUnit;
        private float lastErrorM;
        private float lastQuantumM;

        private const float MinimumSlope = 1e-9f;

        public void Update()
        {
            if (!requestActive) return;
            if (motor == null) return;
            Feed();
        }

        // ---- operator API ------------------------------------------------------

        /// <summary>
        /// Asks for one target diameter. Idempotent: repeating the same target is a no-op, so a
        /// driver may call it every frame while it waits for <see cref="IsConverged"/>.
        /// </summary>
        public void SetDiameterM(float targetMeters)
        {
            if (targetMeters <= 0f) return;
            if (requestActive && Mathf.Abs(targetMeters - targetM) <= 0.000001f) return;
            targetM = targetMeters;
            requestActive = true;
            converged = false;
            absorbsThisRequest = 0;
            absorbCount = 0;
            lastFailure = "";
            lastErrorM = 0f;
            lastQuantumM = 0f;
            nextFeedTime = 0f;
            // The slope belongs to the motor's pricing, not to one request, so it survives across
            // requests. The sample pair must not: the round reset moves the ball without a commit.
            hasSample = false;
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
        /// One volume unit expressed as a diameter change at the last measurement, metres.
        /// Zero until the slope has been measured.
        /// </summary>
        public float GetQuantumM()
        {
            return lastQuantumM;
        }

        /// <summary>d(D^3)/d(volume unit) as measured this request; 0 before the first probe.</summary>
        public float GetCubedSlope()
        {
            return slopeCubedPerUnit;
        }

        public bool IsCalibrated()
        {
            return slopeCubedPerUnit > MinimumSlope;
        }

        // ---- the loop ----------------------------------------------------------

        private void Feed()
        {
            if (converged) return;
            if (motor.GetSemanticDiameterWorldM() <= 0f) return;

            // One candidate in flight: a full queue would latch the motor's overflow counter,
            // and the field asserts that counter is 0.
            if (motor.GetPendingCandidateCount() > 0) return;

            float volumeUnits = motor.GetConfirmedVolumeUnits();
            float diameter = motor.GetSemanticDiameterWorldM();
            float cubed = diameter * diameter * diameter;

            // Measure the slope from consecutive settled samples (the probe is one of them).
            if (hasSample && volumeUnits > lastVolumeUnits && cubed > lastCubedDiameter)
            {
                float measured = (cubed - lastCubedDiameter) / (volumeUnits - lastVolumeUnits);
                if (measured > MinimumSlope) slopeCubedPerUnit = measured;
            }

            hasSample = true;
            lastVolumeUnits = volumeUnits;
            lastCubedDiameter = cubed;

            float error = diameter - targetM;
            lastErrorM = error;
            if (error < 0f) error = -error;

            float quantum = 0f;
            if (slopeCubedPerUnit > MinimumSlope && cubed > 0f && diameter > 0f)
            {
                // dD = d(D^3) / (3 D^2), and 3 D^2 == 3 * cubed / D, so no root is needed.
                quantum = slopeCubedPerUnit * diameter / (3f * cubed);
            }
            lastQuantumM = quantum;

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

            if (absorbsThisRequest >= maxAbsorbsPerRequest)
            {
                Fail("capExceeded");
                return;
            }

            if (Time.time < nextFeedTime) return;

            float targetCubed = targetM * targetM * targetM;
            int units = 1;
            if (slopeCubedPerUnit > MinimumSlope)
            {
                float remaining = targetCubed - cubed;
                float wanted = remaining / slopeCubedPerUnit;
                units = (int)(wanted + 0.5f);
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
