using UdonSharp;
using UnityEngine;
using VRCKatamari.BallMotor;
using VRCKatamari.Core;
using VRCKatamari.Riding;
using VRCKatamari.Story;

namespace VRCKatamari.BallLayers
{
    /// <summary>
    /// Floats a small direction marker ahead of the ball while the local player is riding,
    /// so the push direction is readable before the ball moves.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Pure presentation. It reads three existing public seams and writes one Transform's
    /// world pose and uniform scale: the motor's centre and drawn (semantic) diameter, the
    /// follow rig Transform's world yaw as the push direction, and the ride station's seated
    /// flag. Nothing reads the cue back: it is never submitted as a pickup candidate, never
    /// registered in a blocking array, and the arrow geometry carries no collider and no
    /// rigidbody, so it cannot be absorbed, block anything or be picked. Deleting this
    /// behaviour leaves the ball's behaviour unchanged.
    /// </para>
    /// <para>
    /// Why the follow-rig yaw: the rig is where the rider sits and its yaw is the filtered
    /// heading the seat faces - the surviving "where a forward push goes" signal when the
    /// ball stands still, where the planar velocity is zero (and points backwards while
    /// braking). It is also the seat's own yaw, so the marker and the seat cannot disagree.
    /// BallMotor exposes no public getter for that yaw, so <c>pushYawSource</c> is the same
    /// Transform the operator already wired to the motor's <c>followRigRoot</c>; reading it
    /// requires no change to BallMotor.
    /// </para>
    /// <para>
    /// Visibility: only while the local player is seated
    /// (<see cref="BallRideStation.IsLocalPlayerSeated"/>) and, when a round controller is
    /// wired, only in <see cref="StoryRoundController.StateRunning"/>. A missing round
    /// controller is skipped, so a lab without one still shows the cue; a scene that has a
    /// controller should wire it, otherwise the cue would also show while seated on the
    /// result screen. The cue is hidden by disabling its GameObject, so <c>cueRoot</c> must
    /// be a different object (normally a child of this one), never this behaviour's own
    /// object: disabling that would stop Update and the cue could never reappear.
    /// </para>
    /// <para>
    /// Assembly: own GameObject by project convention, with the arrow hierarchy under a
    /// separate child assigned to <c>cueRoot</c>. The arrow is authored at a 1.0 m design
    /// length along +Z, centred on its origin; this behaviour overwrites the root's local
    /// scale every visible frame, so no parent of <c>cueRoot</c> may carry a scale other
    /// than 1. The operator gives it an unlit translucent material, no shadows and no
    /// colliders. The tuning numbers below are PROVISIONAL.
    /// </para>
    /// <para>
    /// Per-frame cost while visible: two motor getters, one Transform yaw read, one sine, a
    /// handful of multiplications, three Transform writes and - only on a visibility change
    /// - one SetActive. While not seated (or not running) Update returns after the seated
    /// check and never touches the math, the motor or the Transform.
    /// </para>
    /// </remarks>
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class PushDirectionCue : UdonSharpBehaviour
    {
        // ---- Scene links (the operator wires these; a null link hides the cue) ----
        [SerializeField] private KatamariBallMotor motor;

        // The motor's followRigRoot: the yaw the rider's seat faces is the push direction.
        [SerializeField] private Transform pushYawSource;

        // The seat that owns the local seated flag and starts the round on a sit-down.
        [SerializeField] private BallRideStation rideStation;

        // Optional. When wired, the cue is shown only while the round is running.
        [SerializeField] private StoryRoundController roundController;

        // The arrow hierarchy; hidden by SetActive, so it must not be this object.
        [SerializeField] private Transform cueRoot;

        // ---- PROVISIONAL cue geometry, all metres except the two rates ----
        // The cue's centre sits radius + gap ahead of the ball's centre, with the gap
        // growing linearly with the drawn diameter. Length is a uniform scale for an arrow
        // authored 1.0 m long; the rails only stop absurd balls from producing an absurd
        // marker. The bob is one slow sine on Y so the cue reads as floating, not as a prop.
        [SerializeField] private float gapBaseMeters = 0.35f;
        [SerializeField] private float gapPerDiameter = 0.45f;
        [SerializeField] private float hoverAboveBallTopMeters = 0.08f;
        [SerializeField] private float lengthBaseMeters = 0.10f;
        [SerializeField] private float lengthPerDiameter = 0.40f;
        [SerializeField] private float minLengthMeters = 0.05f;
        [SerializeField] private float maxLengthMeters = 2.50f;
        [SerializeField] private float bobAmplitudeMeters = 0.025f;
        [SerializeField] private float bobHz = 0.25f;

        // The last visibility actually written, so a hidden frame costs one compare.
        private bool cueVisible;

        private void Start()
        {
            // Scene authoring may leave the arrow active. Hide it unconditionally: the
            // tracked flag starts false, so ApplyVisibility alone would skip the write and
            // a cue authored active would stay on screen forever.
            cueVisible = false;
            if (cueRoot != null) cueRoot.gameObject.SetActive(false);
        }

        public void Update()
        {
            if (cueRoot == null) return;
            if (!IsCueWanted())
            {
                ApplyVisibility(false);
                return;
            }

            float diameterM = motor.GetSemanticDiameterWorldM();
            // A motor that has not run yet reports 0: there is no drawn ball to float in
            // front of, so stay hidden instead of showing the bare gap.
            if (!KatamariMath.IsFiniteFloat(diameterM) || diameterM <= 0f)
            {
                ApplyVisibility(false);
                return;
            }

            Vector3 cuePosition;
            float cueYaw;
            float cueLength;
            if (!PushCueMath.TryEvaluate(motor.GetCenterWorld(), diameterM,
                pushYawSource.rotation.eulerAngles.y, Time.time,
                gapBaseMeters, gapPerDiameter, hoverAboveBallTopMeters,
                lengthBaseMeters, lengthPerDiameter, minLengthMeters, maxLengthMeters,
                bobAmplitudeMeters, bobHz,
                out cuePosition, out cueYaw, out cueLength))
            {
                ApplyVisibility(false);
                return;
            }

            cueRoot.position = cuePosition;
            cueRoot.rotation = Quaternion.Euler(0f, cueYaw, 0f);
            cueRoot.localScale = Vector3.one * cueLength;
            ApplyVisibility(true);
        }

        /// <summary>
        /// Whether the local player is riding right now, and (if wired) the round is running.
        /// </summary>
        private bool IsCueWanted()
        {
            if (motor == null || pushYawSource == null || rideStation == null) return false;
            if (!rideStation.IsLocalPlayerSeated()) return false;
            if (roundController == null) return true;
            return roundController.GetState() == StoryRoundController.StateRunning;
        }

        /// <summary>
        /// Shows or hides the cue, touching the GameObject only when the state changes.
        /// </summary>
        private void ApplyVisibility(bool visible)
        {
            if (cueRoot == null) return;
            if (cueVisible == visible) return;
            cueVisible = visible;
            cueRoot.gameObject.SetActive(visible);
        }
    }
}
