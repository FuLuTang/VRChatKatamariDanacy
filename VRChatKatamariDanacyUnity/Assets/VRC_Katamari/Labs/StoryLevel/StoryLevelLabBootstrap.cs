using UdonSharp;
using UnityEngine;
using VRCKatamari.BallMotor;

namespace VRCKatamari.Labs.StoryLevel
{
    /// <summary>
    /// Operator aid for the first story level slice: scripted drive, camera trail and a
    /// one-line-per-second report. It registers NOTHING with the motor any more.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The arena belongs to <see cref="VRCKatamari.Story.LevelArenaHost"/>: ground plane,
    /// boundary walls, static boxes and the kind whitelist are that component's job
    /// (Docs/Design/BallMotor/TERRAIN_SUPPORT.md §7.1/§7.6). Keeping a second writer here
    /// would mean two components registering the same floor and the same boxes, and the
    /// lab's copy would silently win or lose depending on Update order. The file and
    /// class name are unchanged on purpose: the scene still references this script, and
    /// the rename/move is a separate scene migration.
    /// </para>
    /// <para>
    /// What remains is what a lab is for: a timeline that exercises the real input seam,
    /// a camera that trails the ball, and the acceptance log. A positive semantic
    /// diameter is the public proof that the motor is ready, so reporting waits for it
    /// instead of racing Start order.
    /// </para>
    /// <para>
    /// The log carries two height numbers on purpose:
    /// <c>bottom</c> is the ball's lowest core point in world Y (<c>centre.y - collision
    /// radius</c>), which is what the old acceptance line pinned (on a 0.26 m platform it
    /// must read 0.260); <c>gap</c> is that bottom above the selected support surface,
    /// which is the number that proves the ball actually sits ON the step (0.000 ±0.005)
    /// and goes positive while it falls. Reporting only one of them would either break
    /// the old check or hide a float/sink.
    /// </para>
    /// </remarks>
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class StoryLevelLabBootstrap : UdonSharpBehaviour
    {
        [SerializeField] private KatamariBallMotor motor;
        [SerializeField] private Transform cameraFollow;
        [SerializeField] private Vector3 cameraOffset = new Vector3(0f, 3.2f, -5.5f);
        [SerializeField] private Vector3 cameraEuler = new Vector3(24f, 0f, 0f);

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
            // Nothing here touches the motor: its Start may not have run yet, and its
            // stores are allocated there.
        }

        public void Update()
        {
            if (!initialised)
            {
                if (!MotorReady()) return;
                startDiameter = motor.GetSemanticDiameterWorldM();
                elapsed = 0f;
                nextLogTime = 0f;
                initialised = true;
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
        /// One line per second: elapsed, centre, heading, diameter, the ball's bottom,
        /// the gap along the selected support normal, which surface that is, and whether the
        /// ball is falling. See the class remarks for why bottom and gap are both here.
        /// </summary>
        private void Report()
        {
            Vector3 center = motor.GetCenterWorld();
            float diameter = motor.GetSemanticDiameterWorldM();
            float supportHeight = motor.GetSupportSurfaceHeightWorldM();
            Vector3 supportNormal = motor.GetSupportNormalWorld();
            // Gap along the support normal (centre to plane, minus radius), the same reading
            // StoryBallDiagnostics prints. R11: the vertical form reads r(1/n.y - 1) on a ramp,
            // which looks like the ball floats; on flat ground n.y = 1 and the number is the
            // old one (up to float association order).
            float normalY = supportNormal.y;
            if (!(normalY > 0.001f) || normalY > 1.0001f) normalY = 1f;
            float radius = motor.GetCollisionRadiusWorldM();
            float bottom = center.y - radius;
            float gap = (center.y - supportHeight) * normalY - radius;
            // "ground" / "box" / "prop" reads better than the raw id in the acceptance log;
            // the id is still printed because it names WHICH box or prop top when several
            // are registered. The kind comes from the motor, not from a range test on the
            // id: prop tops are 100+i, so "id > 0" would mislabel one as a box.
            int supportId = motor.GetSupportSurfaceId();
            int supportKind = motor.GetSupportKind();
            string supportName = "none";
            if (supportKind == 0) supportName = "ground";
            else if (supportKind == 1) supportName = "box";
            else if (supportKind == 2) supportName = "ramp";
            else if (supportKind == 3) supportName = "prop";
            Vector3 axes = motor.GetPlanarVelocity();
            Debug.Log("[story-lab] t=" + (Mathf.Floor(elapsed * 10f) / 10f)
                + " center=" + center
                + " heading=" + (Mathf.Floor(motor.GetHeadingDegrees() * 10f) / 10f)
                + " diameter=" + (Mathf.Floor(diameter * 10000f) / 10000f)
                + " bottom=" + (Mathf.Floor(bottom * 1000f) / 1000f)
                + " gap=" + (Mathf.Floor(gap * 1000f) / 1000f)
                + " support=" + supportName
                + " supportId=" + supportId
                + " supportY=" + (Mathf.Floor(supportHeight * 1000f) / 1000f)
                + " supports=" + motor.GetSupportSurfaceCount()
                + " stepLimit=" + (Mathf.Floor(motor.GetStepHeightLimitWorldM() * 1000f) / 1000f)
                + " falling=" + motor.IsFalling()
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
