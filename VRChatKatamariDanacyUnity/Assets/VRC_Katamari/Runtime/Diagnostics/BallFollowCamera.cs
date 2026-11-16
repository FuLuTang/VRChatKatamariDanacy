using UdonSharp;
using UnityEngine;
using VRCKatamari.BallMotor;
using VRCKatamari.Core;

namespace VRCKatamari.Diagnostics
{
    /// <summary>
    /// DEV-ONLY third-person ball camera: a world camera that looks at the ball from behind
    /// and above, rendered into a RenderTexture and shown on a screen-space overlay.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Why it exists: the seat camera the ride rig owns looks over the ball (about 1.05 m above
    /// it), so a screenshot from the player's own view never contains the ball, and an
    /// acceptance run cannot show that props are stuck to it. VRChat exposes no way to move or
    /// replace the player's view - <c>VRCCameraSettings.ScreenCamera</c> position and rotation
    /// are read-only - so the only supported route is a second camera rendering to a
    /// RenderTexture, shown by a RawImage the local player can always see. This behaviour drives
    /// that camera. Operator steps: Docs/Design/BallPresentation/DEBUG_CAMERA.md.
    /// </para>
    /// <para>
    /// It changes nothing about gameplay and nothing about the ride rig. It only ever reads the
    /// motor (centre, heading, collision radius) and writes its own Camera, its own inset Camera
    /// and the two overlay roots. It never calls a motor setter, never touches the station, the
    /// seat, the follow rig or any gate, and it holds no reference to any of them.
    /// </para>
    /// <para>
    /// Off is the release state and it is inert: <c>LateUpdate</c> polls the two toggle keys and
    /// returns before reading the motor or either Camera, so nothing is written and nothing is
    /// allocated. The only writes made while off are the one-time dark state in <c>Start</c> and
    /// <c>OnDisable</c>, which force the rig off so a scene saved with it enabled cannot leak
    /// into a build.
    /// </para>
    /// <para>
    /// Framing is a multiple of the ball radius (see <see cref="DebugCameraMath"/>), so the same
    /// constants frame a 0.15 m starting ball and a 30 m map-sized one identically. The optional
    /// inset is a second Camera parented to this one: it inherits the pose and shows a tighter
    /// crop, and its culling mask is authored in the scene, which is how it can be made
    /// ball-only. Toggling is <c>Interact</c> (desktop: left mouse on the rig's collider) or the
    /// keyboard keys below.
    /// </para>
    /// </remarks>
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class BallFollowCamera : UdonSharpBehaviour
    {
        // ---- Scene links (the operator wires these) ----
        [Tooltip("Authoritative target. Read-only: centre, heading and collision radius.")]
        [SerializeField] private KatamariBallMotor motor;

        [Tooltip("Fallback target when no motor is wired; its position and horizontal forward are used.")]
        [SerializeField] private Transform ballTransform;

        [Tooltip("Radius used with ballTransform, metres. Ignored when the motor is wired.")]
        [SerializeField] private float fallbackRadiusM = 0.15f;

        [Tooltip("The world camera that renders into the main RenderTexture.")]
        [SerializeField] private Camera debugCamera;

        [Tooltip("Optional tighter camera, parented to debugCamera so it inherits the pose.")]
        [SerializeField] private Camera insetCamera;

        [Tooltip("RawImage holder for the main view; enabled only while the rig is on.")]
        [SerializeField] private GameObject overlayRoot;

        [Tooltip("RawImage holder for the ball-only inset; enabled only while the inset is on.")]
        [SerializeField] private GameObject insetRoot;

        // ---- Framing, all multiples of the ball radius ----
        [Header("Framing (multiples of the ball radius)")]
        [Tooltip("Camera distance behind the ball, in radii. 2.6 keeps the ball and its props in frame.")]
        [SerializeField] private float backRadiusFactor = 2.6f;

        [Tooltip("Camera height above the ball centre, in radii. 1.4 looks down on the absorbed props.")]
        [SerializeField] private float upRadiusFactor = 1.4f;

        [Tooltip("Aim point above the ball centre, in radii. 0.4 puts the ball in the lower-middle third.")]
        [SerializeField] private float aimLiftRadiusFactor = 0.4f;

        [Tooltip("Vertical field of view, degrees. Fixed, because the offset already scales with radius.")]
        [SerializeField] private float fieldOfViewDegrees = 60f;

        [Tooltip("Vertical field of view of the inset camera, degrees. Tighter crop of the same pose.")]
        [SerializeField] private float insetFieldOfViewDegrees = 50f;

        [Tooltip("Write near/far from the ball radius every frame instead of using the authored values.")]
        [SerializeField] private bool autoClipPlanes = true;

        // ---- DEV ONLY: the switch ----
        [Header("DEV ONLY - debug ball camera (leave off in release)")]
        [Tooltip("Master switch, default off. Off means no camera, no overlay and no per-frame writes.")]
        [SerializeField] private bool debugCameraEnabled = false;

        [Tooltip("Also show the tight inset view. Only meaningful while the master switch is on.")]
        [SerializeField] private bool insetEnabled = false;

        [Tooltip("Desktop keyboard toggle for the master switch. Web/mobile clients have no keyboard.")]
        [SerializeField] private KeyCode toggleKey = KeyCode.F2;

        [Tooltip("Desktop keyboard toggle for the inset.")]
        [SerializeField] private KeyCode insetToggleKey = KeyCode.F3;

        // Published rig state: what the scene is showing right now. Kept so the transitions log
        // once and the steady state writes nothing.
        private bool published;
        private bool publishedInset;
        private bool cameraWarned;
        private bool targetWarned;

        private void Start()
        {
            // The release pose is "rig dark". Forcing it once here means a scene saved with the
            // camera enabled still cannot render a full second view until an operator turns it
            // on; after this, off writes nothing at all.
            ForceRigDark();
        }

        // OnDisable is a Unity lifecycle message forward to Udon, not a member of
        // UdonSharpBehaviour, so it is an ordinary method and must not be override.
        private void OnDisable()
        {
            ForceRigDark();
        }

        /// <summary>World interaction (desktop: left mouse on this object's collider).</summary>
        /// <remarks>
        /// The only toggle available in a real VRChat client without a keyboard, which is why the
        /// rig carries a collider in the scene. It flips the same flag the key does.
        /// </remarks>
        public override void Interact()
        {
            debugCameraEnabled = !debugCameraEnabled;
        }

        // LateUpdate is a Unity lifecycle message forward to Udon (the VM registers _lateUpdate),
        // so it is an ordinary method and must not be override. It runs after the motor's Update,
        // so the camera reads this frame's ball pose rather than the previous one.
        private void LateUpdate()
        {
            // The keys are the whole off-path cost: two static calls that return false while the
            // rig is dark. They are polled even when off, because that is how it turns on.
            if (UnityEngine.Input.GetKeyDown(toggleKey)) debugCameraEnabled = !debugCameraEnabled;
            if (UnityEngine.Input.GetKeyDown(insetToggleKey)) insetEnabled = !insetEnabled;

            SyncRig();
            if (!published) return;

            if (debugCamera == null)
            {
                if (!cameraWarned)
                {
                    cameraWarned = true;
                    Debug.LogWarning("[camera] debug ball camera: no Camera wired; nothing to drive");
                }
                return;
            }

            Vector3 center;
            float radius;
            Vector3 forward;
            if (!TryReadTarget(out center, out radius, out forward))
            {
                // The motor has no size before it starts. Keep the last pose instead of snapping
                // the camera to the world origin, and say so once.
                if (!targetWarned)
                {
                    targetWarned = true;
                    Debug.Log("[camera] debug ball camera: no usable target yet;"
                        + " the view keeps its last pose");
                }
                return;
            }

            Vector3 position;
            Quaternion rotation;
            float distanceM;
            if (!DebugCameraMath.TryComputePose(center, forward, radius, backRadiusFactor,
                upRadiusFactor, aimLiftRadiusFactor, out position, out rotation, out distanceM))
            {
                return;
            }

            debugCamera.transform.position = position;
            debugCamera.transform.rotation = rotation;
            debugCamera.fieldOfView = fieldOfViewDegrees;

            float nearM;
            float farM;
            if (autoClipPlanes && DebugCameraMath.TryClipPlanes(distanceM, radius, out nearM, out farM))
            {
                debugCamera.nearClipPlane = nearM;
                debugCamera.farClipPlane = farM;
            }

            if (publishedInset && insetCamera != null)
            {
                insetCamera.fieldOfView = insetFieldOfViewDegrees;
            }
        }

        /// <summary>
        /// Publishes the wanted on/off state to the scene, logging only the transitions.
        /// </summary>
        /// <remarks>
        /// A missing camera counts as "off": the rig reports the state it can actually show, so a
        /// half-wired scene never claims to be on. The inset follows the master switch, which is
        /// why it is computed here rather than from its own flag alone.
        /// </remarks>
        private void SyncRig()
        {
            bool want = debugCameraEnabled && debugCamera != null;
            if (want != published)
            {
                published = want;
                if (debugCamera != null) debugCamera.enabled = want;
                if (overlayRoot != null) overlayRoot.SetActive(want);
                if (want) Debug.Log("[camera] debug ball camera on");
                else Debug.Log("[camera] debug ball camera off");
            }

            bool wantInset = want && insetEnabled;
            if (wantInset != publishedInset)
            {
                publishedInset = wantInset;
                if (insetCamera != null) insetCamera.enabled = wantInset;
                if (insetRoot != null) insetRoot.SetActive(wantInset);
                if (wantInset) Debug.Log("[camera] ball-only inset on");
                else Debug.Log("[camera] ball-only inset off");
            }
        }

        private void ForceRigDark()
        {
            published = false;
            publishedInset = false;
            cameraWarned = false;
            targetWarned = false;
            if (debugCamera != null) debugCamera.enabled = false;
            if (overlayRoot != null) overlayRoot.SetActive(false);
            if (insetCamera != null) insetCamera.enabled = false;
            if (insetRoot != null) insetRoot.SetActive(false);
        }

        /// <summary>
        /// Reads the ball's centre, horizontal travel direction and collision radius.
        /// </summary>
        /// <remarks>
        /// The motor is the authority because it already publishes exactly the three numbers the
        /// camera needs; the transform fallback exists for a lab with no motor. Both reject a
        /// radius of zero, which is what an unstarted motor reports, so the caller keeps its pose
        /// instead of framing a degenerate ball.
        /// </remarks>
        private bool TryReadTarget(out Vector3 center, out float radius, out Vector3 forward)
        {
            center = Vector3.zero;
            radius = 0f;
            forward = Vector3.forward;

            if (motor != null)
            {
                center = motor.GetCenterWorld();
                radius = motor.GetCollisionRadiusWorldM();
                float headingDegrees = motor.GetHeadingDegrees();
                if (!KatamariMath.IsFiniteFloat(headingDegrees)) return false;
                // The motor derives its forward as (+sin, +cos) of the heading; the camera sits
                // behind that direction, never behind the ball's roll.
                float headingRadians = headingDegrees * Mathf.Deg2Rad;
                forward = new Vector3(Mathf.Sin(headingRadians), 0f, Mathf.Cos(headingRadians));
                return KatamariMath.IsFiniteFloat(radius) && radius > 0f;
            }

            if (ballTransform == null) return false;
            center = ballTransform.position;
            radius = fallbackRadiusM;
            forward = ballTransform.forward;
            return KatamariMath.IsFiniteFloat(radius) && radius > 0f;
        }
    }
}
