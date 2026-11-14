using UdonSharp;
using UnityEngine;
using VRCKatamari.BallMotor;

namespace VRCKatamari.Ai
{
    /// <summary>
    /// Drives one CPU ball through the motor's public interface only.
    /// </summary>
    /// <remarks>
    /// <para>
    /// PROJECT-CHOSEN IMPLEMENTATION, NOT A RECONSTRUCTION OF THE ORIGINAL AI. No
    /// published source describes how the original game's CPU picks targets, so this
    /// is our own minimal policy written against the confirmed rules in
    /// Docs/Research/KatamariBallRules (C1/C3/C4 conversion area, C5 shrinking costs
    /// reach, C6/C9 bigger balls are dangerous, C10 score wins).
    /// </para>
    /// <para>
    /// Interface discipline: only public getters are read; only SetMoveAxes and
    /// TryBank are written. The pilot builds no log lines - it exposes data and the
    /// scene host prints one line per ball per second, so a ball's state is never
    /// split across two log formats.
    /// </para>
    /// </remarks>
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class CpuBallPilot : UdonSharpBehaviour
    {
        [SerializeField] private KatamariBallMotor motor;
        [SerializeField] private string pilotName = "cpu-1";

        [Header("Targets this pilot may chase")]
        [SerializeField] private Transform[] targetProps;

        [Header("Conversion (rule C1/C3/C4)")]
        [SerializeField] private Transform bankZone;
        [SerializeField] private float bankDiameterM = 0.55f;
        [SerializeField] private float bankRequestIntervalSeconds = 2f;

        [Header("Steering")]
        [SerializeField] private float ramRangeM = 1.2f;
        [SerializeField] private float aimConeDegrees = 12f;
        [SerializeField] private float overshootM = 0.6f;
        // Approach speed while turning. Orbit radius is r = v / omega, so this is the
        // parameter that decides whether the ball can ever get inside reach: at
        // adjustForward 0.2 the measured v was 0.54 m/s against omega ~1.82 rad/s, i.e.
        // a 0.30 m orbit that sat just outside the 0.279 m reach. Halving the commanded
        // forward targets r ~ 0.15 m.
        [SerializeField] private float adjustForward = 0.1f;
        [SerializeField] private float turnFullScaleDegrees = 45f;
        [SerializeField] private float turnDeadbandDegrees = 8f;
        [SerializeField] private float turnaroundDegrees = 90f;
        [SerializeField] private float chargeForward = 0.6f;

        [Header("Target stickiness")]
        [SerializeField] private float stallSeconds = 3f;

        [Header("Avoidance (rule C6/C9): rivals larger than this ratio")]
        [SerializeField] private KatamariBallMotor[] rivals;
        [SerializeField] private float dangerRatio = 1.25f;
        [SerializeField] private float dangerRadiusM = 3f;

        private Transform target;
        private float stalledTime;
        private float closestDistance;
        private float lastTargetDistance;
        private float nextBankTime;
        private int targetSwitches;
        private float lastHorizontalError;
        private float lastDelta;
        private float lastForward;

        public void Update()
        {
            if (motor == null) return;

            Vector3 center = motor.GetCenterWorld();
            float diameter = motor.GetSemanticDiameterWorldM();

            if (FleeFromDanger(center, diameter)) return;

            // Rule C1/C3/C4: once big enough, convert at the bank zone.
            if (diameter >= bankDiameterM && bankZone != null)
            {
                target = null;
                Steer(center, bankZone.position);
                if (Vector3.Distance(center, bankZone.position) <= 1.5f
                    && Time.time >= nextBankTime)
                {
                    nextBankTime = Time.time + bankRequestIntervalSeconds;
                    motor.TryBank(Time.time, pilotName + "-bank");
                }
                return;
            }

            AcquireOrKeepTarget(center);
            if (target == null) return;
            Steer(center, target.position);
        }

        /// <summary>
        /// Keeps the current target until it is absorbed, gone, or no longer being
        /// approached. Re-picking the nearest item every frame made the ball oscillate
        /// between two neighbours instead of lining up with either.
        /// </summary>
        private void AcquireOrKeepTarget(Vector3 center)
        {
            if (target != null)
            {
                bool gone = !target.gameObject.activeSelf;
                float distance = Vector3.Distance(center, target.position);
                if (distance < lastTargetDistance - 0.001f) stalledTime = 0f;
                else stalledTime = stalledTime + Time.deltaTime;
                lastTargetDistance = distance;
                if (distance < closestDistance) closestDistance = distance;

                // Passed it: the heading lock drove us by. Give up this target rather than
                // turning around and re-committing to the one we already missed.
                bool overshot = distance > closestDistance + overshootM;
                if (gone || overshot || stalledTime >= stallSeconds)
                {
                    target = PickNearest(center);
                    targetSwitches = targetSwitches + 1;
                    stalledTime = 0f;
                    closestDistance = 999f;
                    lastTargetDistance = target == null ? 0f : Vector3.Distance(center, target.position);
                }
                return;
            }

            target = PickNearest(center);
            stalledTime = 0f;
            closestDistance = 999f;
            lastTargetDistance = target == null ? 0f : Vector3.Distance(center, target.position);
        }

        private Transform PickNearest(Vector3 center)
        {
            if (targetProps == null) return null;
            Transform best = null;
            float bestDistance = 0f;
            for (int i = 0; i < targetProps.Length; i++)
            {
                Transform candidate = targetProps[i];
                if (candidate == null) continue;
                if (!candidate.gameObject.activeSelf) continue;
                float distance = Vector3.Distance(center, candidate.position);
                if (best == null || distance < bestDistance)
                {
                    best = candidate;
                    bestDistance = distance;
                }
            }
            return best;
        }

        private bool FleeFromDanger(Vector3 center, float diameter)
        {
            if (rivals == null) return false;
            Vector3 threat = Vector3.zero;
            bool found = false;
            for (int i = 0; i < rivals.Length; i++)
            {
                KatamariBallMotor rival = rivals[i];
                if (rival == null || rival == motor) continue;
                float rivalDiameter = rival.GetSemanticDiameterWorldM();
                if (rivalDiameter <= diameter * dangerRatio) continue;
                Vector3 delta = center - rival.GetCenterWorld();
                if (delta.magnitude > dangerRadiusM) continue;
                threat = center + delta.normalized * dangerRadiusM;
                found = true;
            }
            if (!found) return false;
            target = null;
            Steer(center, threat);
            return true;
        }

        /// <summary>
        /// Aims, then drives straight in without correcting the heading.
        /// </summary>
        /// <remarks>
        /// Stop-and-turn failed for a measured reason: reach is only ~0.28 m, so the ball
        /// must arrive within a few centimetres of the line. Holding forward at 0 and
        /// turning until an 8-degree deadband let the ball start moving, lose the heading
        /// in one step, and stop again - |v| swung 0.11 / 0.76 / 0 in the logs while the
        /// distance stalled at 0.38-0.49 m. Now: outside a 12-degree cone (or far away)
        /// it turns slowly with a small forward nudge; inside the cone it locks the
        /// heading (turn = 0) and runs at full speed straight through the item. A 12-degree
        /// cone at 1.2 m is ~0.25 m of lateral error, which is about reach - tight enough
        /// to arrive, loose enough to actually leave the spot.
        /// </remarks>
        private void Steer(Vector3 center, Vector3 destination)
        {
            float dx = destination.x - center.x;
            float dz = destination.z - center.z;
            float distance = Mathf.Sqrt(dx * dx + dz * dz);
            lastHorizontalError = distance;
            if (distance < 0.0001f) return;

            float desired = Mathf.Atan2(dx, dz) * Mathf.Rad2Deg;
            float delta = Mathf.DeltaAngle(motor.GetHeadingDegrees(), desired);
            lastDelta = delta;

            bool aligned = Mathf.Abs(delta) < aimConeDegrees;
            bool closeEnough = distance <= ramRangeM;

            if (aligned && closeEnough)
            {
                // Committed: no steering while running in, or it curves past the item.
                lastForward = 1f;
                motor.SetMoveAxes(1f, 0f, 0f);
                return;
            }

            float turn = Mathf.Clamp(delta / turnFullScaleDegrees, -1f, 1f);
            if (Mathf.Abs(delta) < turnDeadbandDegrees) turn = 0f;
            lastForward = adjustForward;
            motor.SetMoveAxes(adjustForward, turn, 0f);
        }

        // ---- Read-only seams; the scene host formats the one line per ball ----
        public int GetTargetSwitchCount()
        {
            return targetSwitches;
        }

        public float GetHorizontalErrorM()
        {
            return lastHorizontalError;
        }

        public float GetHeadingDeltaDegrees()
        {
            return lastDelta;
        }

        public float GetForwardInput()
        {
            return lastForward;
        }

        public UnityEngine.Transform GetTargetTransform()
        {
            return target;
        }

        public string GetTargetName()
        {
            if (target == null) return "none";
            return target.gameObject.name;
        }

        public string GetPilotName()
        {
            return pilotName;
        }
    }
}
