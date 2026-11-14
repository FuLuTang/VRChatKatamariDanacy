using UdonSharp;
using UnityEngine;
using VRCKatamari.BallMotor;

namespace VRCKatamari.Labs.StoryLevel
{
    /// <summary>
    /// Wiring for the first story level slice: register the floor and walls with the ball
    /// motor, open the round, and trail the camera. No rules live here.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Scope is deliberately one ball, one floor, four walls. Round state, settlement and
    /// scoring are NOT here: the story mode's own controller is a later task, and a second
    /// copy of that logic in a lab is the duplicate this project keeps paying for.
    /// </para>
    /// <para>
    /// Registration is deferred to the first Update because Unity does not promise that this
    /// component's Start runs after the motor's, and the motor allocates its stores in Start.
    /// A positive diameter is the public proof that the motor is ready.
    /// </para>
    /// </remarks>
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class StoryLevelLabBootstrap : UdonSharpBehaviour
    {
        [SerializeField] private KatamariBallMotor motor;
        [SerializeField] private Transform cameraFollow;
        [SerializeField] private Vector3 cameraOffset = new Vector3(0f, 3.2f, -5.5f);
        [SerializeField] private Vector3 cameraEuler = new Vector3(24f, 0f, 0f);

        [Header("Ground / obstacles")]
        [SerializeField] private Vector3 groundNormal = new Vector3(0f, 1f, 0f);
        [SerializeField] private float groundOffset = 0f;
        [SerializeField] private BoxCollider[] obstacleBoxes;

        [Header("Kinds")]
        [SerializeField] private string[] allowedKinds;

        [Header("Self-test drive (operator aid, off by default)")]
        // The operator cannot press keys, and calling SetMoveAxes from C# does not reach the
        // Udon heap (verified: axes stayed 0 and the ball did not move). This timeline is the
        // only way to exercise the real input path from the editor: forward 2 s, forward+turn
        // 2 s, then release 2 s. It is off unless a lab explicitly turns it on.
        [SerializeField] private bool scriptedDrive = false;
        [SerializeField] private float scriptedForwardSeconds = 2f;
        [SerializeField] private float scriptedTurnSeconds = 2f;

        [Header("Reporting")]
        [SerializeField] private float logIntervalSeconds = 1f;

        private bool initialised;
        private float elapsed;
        private float nextLogTime;
        private float startDiameter;

        public void Start()
        {
            // Nothing here touches the motor: its Start may not have run yet.
        }

        public void Update()
        {
            if (!initialised)
            {
                if (!MotorReady()) return;
                Setup();
                if (!initialised) return;
            }

            elapsed = elapsed + Time.deltaTime;
            if (scriptedDrive) DriveScripted();
            FollowBall();

            if (elapsed < nextLogTime) return;
            nextLogTime = elapsed + logIntervalSeconds;
            Report();
        }

        /// <summary>
        /// Drives the motor through its public input seam on a fixed timeline.
        /// </summary>
        /// <remarks>
        /// Three phases: forward only, forward with a right turn, then release. The release
        /// phase is what shows the ball decelerating instead of stopping dead.
        /// </remarks>
        private void DriveScripted()
        {
            if (elapsed < scriptedForwardSeconds)
            {
                motor.SetMoveAxes(1f, 0f, 0f);
                return;
            }
            if (elapsed < scriptedForwardSeconds + scriptedTurnSeconds)
            {
                motor.SetMoveAxes(1f, 1f, 0f);
                return;
            }
            if (elapsed < scriptedForwardSeconds + scriptedTurnSeconds + 2f)
            {
                motor.SetMoveAxes(0f, 0f, 0f);
                return;
            }
            motor.SetMoveAxes(0f, 0f, 0f);
        }

        /// <summary>True once the motor reports a started ball (diameter above zero).</summary>
        private bool MotorReady()
        {
            if (motor == null) return false;
            return motor.GetSemanticDiameterWorldM() > 0f;
        }

        private void Setup()
        {
            int kindCount = allowedKinds == null ? 0 : allowedKinds.Length;
            motor.SetAllowedKinds(allowedKinds, kindCount);
            motor.AddPlane(groundNormal, groundOffset);

            if (obstacleBoxes != null)
            {
                for (int i = 0; i < obstacleBoxes.Length; i++)
                {
                    BoxCollider box = obstacleBoxes[i];
                    if (box == null) continue;
                    Bounds b = box.bounds;
                    motor.AddBox(b.min, b.max);
                }
            }

            motor.TryBeginPlaying(Time.time);
            startDiameter = motor.GetSemanticDiameterWorldM();
            elapsed = 0f;
            nextLogTime = 0f;
            initialised = true;

            Debug.Log("[story-lab] setup kinds=" + kindCount
                + " startDiameter=" + startDiameter
                + " boxes=" + (obstacleBoxes == null ? 0 : obstacleBoxes.Length)
                + " playing=" + (motor.GetConfirmedVolumeUnits() >= 0f));
        }

        /// <summary>Trails the ball: position and heading only, never the roll rotation.</summary>
        private void FollowBall()
        {
            if (cameraFollow == null) return;
            Vector3 center = motor.GetCenterWorld();
            float diameter = motor.GetSemanticDiameterWorldM();
            float pullBack = diameter * 1.4f;
            Vector3 offset = cameraOffset;
            offset.z = offset.z - pullBack;
            offset.y = offset.y + pullBack * 0.45f;
            cameraFollow.position = center + offset;
            cameraFollow.rotation = Quaternion.Euler(cameraEuler);
        }

        /// <summary>
        /// One line per second: elapsed, centre, heading, diameter and the ball's bottom
        /// height. "bottom" is the number that proves the ball sits on the floor rather than
        /// floating or sinking, and it is the check the size rule is judged by.
        /// </summary>
        private void Report()
        {
            Vector3 center = motor.GetCenterWorld();
            float diameter = motor.GetSemanticDiameterWorldM();
            float bottom = center.y - diameter * 0.5f;
            Vector3 axes = motor.GetPlanarVelocity();
            Debug.Log("[story-lab] t=" + (Mathf.Floor(elapsed * 10f) / 10f)
                + " center=" + center
                + " heading=" + (Mathf.Floor(motor.GetHeadingDegrees() * 10f) / 10f)
                + " diameter=" + (Mathf.Floor(diameter * 10000f) / 10000f)
                + " bottom=" + (Mathf.Floor(bottom * 1000f) / 1000f)
                + " axes=" + (Mathf.Floor(axes.x * 100f) / 100f) + "," + (Mathf.Floor(axes.z * 100f) / 100f)
                + " absorbed=" + motor.GetAbsorbedCount()
                + " rejects=" + motor.GetRejectEventCount());
        }

        // ---- Read-only seams for the operator ----
        public float GetStartDiameter()
        {
            return startDiameter;
        }
    }
}
