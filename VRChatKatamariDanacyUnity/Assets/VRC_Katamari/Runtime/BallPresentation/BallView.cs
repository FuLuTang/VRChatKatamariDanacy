using UdonSharp;
using UnityEngine;
using VRCKatamari.BallMotor;

namespace VRCKatamari.BallPresentation
{
    /// <summary>
    /// Draws one ball's core mesh at the diameter the motor reports.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The motor owns the ball root's position and rotation and never writes scale.
    /// This behaviour owns exactly one field: the local scale of the dedicated
    /// <c>coreMesh</c> child. It never scales the rolling root, whose scale must stay
    /// 1 (Docs/02_TECHNICAL_CONTRACTS.md section 2), and never scales the attachment root,
    /// because absorbed props must not grow with the core.
    /// </para>
    /// <para>
    /// Size source: <c>GetSemanticDiameterWorldM()</c>, the narrative size, not the
    /// core or collision radius. The value is a public getter, so this layer keeps
    /// no size state of its own beyond the value it last wrote and the spring's own
    /// two scalars. It never writes anything back to the motor: the physical radius,
    /// the absorb gate, the growth ledger and the follow-rig camera keep reading the
    /// motor's own numbers, so a presentation setting can never move the ball.
    /// </para>
    /// <para>
    /// <b>0.95 visual radius</b> (<c>useVisualCoreRadius</c>, on by default). Section 4A.2
    /// of Docs/Design/BallMotor/CONTACT_MODEL_V2.md defines three shapes and
    /// <c>r_visual = 0.95 * r_phys</c>: the core mesh is the slightly smaller ball that
    /// absorbed props stick out of, while the contact solver keeps the physical sphere.
    /// The motor already exposes that radius (<c>GetVisualCoreRadiusWorldM()</c>), and the
    /// mesh is drawn at <c>2 * r_visual</c>. Off restores the pre-4A behaviour (mesh drawn at
    /// the physical diameter, 5% larger than the design).
    /// </para>
    /// <para>
    /// <b>Short catch-up spring</b> (<c>smoothCoreGrowth</c>, on by default). The motor
    /// re-derives the diameter in the same fixed step that commits an absorb, so a raw copy
    /// turns every absorb into a step. The spring (<see cref="BallPresentationMath"/>, time
    /// constant 0.08 s, critically damped) makes that read as the ball being inflated: it
    /// never crosses the target, it snaps bit-exactly onto it once it is within 1e-5 m, and a
    /// target that collapsed by more than half (a new round, a respawn) snaps instead of
    /// being animated. Off restores the dead-zone-only copy this layer used before.
    /// </para>
    /// <para>
    /// It writes no rotation: visual roll belongs to the visual-roll layer, which
    /// already runs inside the motor. A motor that has not run its Start yet reads
    /// diameter 0, which is skipped instead of written, so Start order does not
    /// matter here.
    /// </para>
    /// <para>
    /// Assembly: sits on its own GameObject by project convention, for clear ownership
    /// and a readable one-line self-check. Sharing a GameObject with another
    /// UdonSharpBehaviour is legal; the real hard constraint there is the sync mode
    /// (co-located behaviours fall back to the strictest one). <c>coreMesh</c> must be
    /// a child of <c>BallVisualRollRoot</c> whose own scale nothing else writes.
    /// </para>
    /// </remarks>
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class BallView : UdonSharpBehaviour
    {
        // A re-scale below this is not visible and would only dirty the transform
        // every frame.
        private const float diameterChangeM = 0.0001f;

        // Used when a scene leaves smoothTimeSeconds at zero: the design value.
        private const float defaultSmoothTimeSeconds = 0.08f;

        [SerializeField] private KatamariBallMotor motor;
        [SerializeField] private Transform coreMesh;

        [Header("Visual size")]
        [Tooltip("Draw the core at 0.95 x the physical radius (CONTACT_MODEL_V2 4A.2), so absorbed props stick out of it. Off draws the full physical diameter.")]
        [SerializeField] private bool useVisualCoreRadius = true;

        [Tooltip("Catch a size change with a short spring instead of jumping to it. Presentation only; the physical radius, the absorb gate and the camera are untouched.")]
        [SerializeField] private bool smoothCoreGrowth = true;

        [Tooltip("Critically damped catch-up time in seconds. 0.08 s is 96% caught up at 200 ms and bit-exact on target by about 0.42 s. A non-positive value falls back to 0.08.")]
        [SerializeField] private float smoothTimeSeconds = defaultSmoothTimeSeconds;

        // The last diameter actually written; a motor that never started leaves it
        // negative, which is also how the first write gets through.
        private float lastDiameterM = -1f;

        // Spring state: the diameter currently drawn and its rate of change, metres/s.
        // Zero means "no previous value", which the spring treats as a snap.
        private float visualDiameterM;
        private float springVelocityMps;

        public void Update()
        {
            if (motor == null || coreMesh == null) return;

            float physicalDiameterM = motor.GetSemanticDiameterWorldM();
            // Read the visual radius only when it is used: a disabled feature should cost
            // nothing, and the motor call is a cross-behaviour call.
            float visualRadiusM = 0f;
            if (useVisualCoreRadius) visualRadiusM = motor.GetVisualCoreRadiusWorldM();

            float targetDiameterM;
            if (!BallPresentationMath.TryCoreTargetDiameter(physicalDiameterM, visualRadiusM,
                useVisualCoreRadius, out targetDiameterM)) return;

            if (!smoothCoreGrowth)
            {
                visualDiameterM = targetDiameterM;
                springVelocityMps = 0f;
                WriteDiameter(targetDiameterM, false);
                return;
            }

            float catchUpSeconds = smoothTimeSeconds;
            if (!(catchUpSeconds > 0f)) catchUpSeconds = defaultSmoothTimeSeconds;

            float nextDiameterM;
            float nextVelocityMps;
            if (!BallPresentationMath.TryStepCoreDiameter(visualDiameterM, springVelocityMps,
                targetDiameterM, Time.deltaTime, catchUpSeconds,
                out nextDiameterM, out nextVelocityMps)) return;

            visualDiameterM = nextDiameterM;
            springVelocityMps = nextVelocityMps;
            // The spring snaps bit-exactly onto the target, so the settling write passes the
            // dead zone and leaves the exact design diameter behind; later frames are no-ops.
            // A zero is never written: scale 0 would make the ball disappear for a frame.
            if (nextDiameterM > 0f) WriteDiameter(visualDiameterM, nextDiameterM == targetDiameterM);
        }

        /// <summary>
        /// Writes the core scale when the change is visible, or when <paramref name="exact"/>
        /// asks for a write that must not be swallowed by the dead zone.
        /// </summary>
        private void WriteDiameter(float diameterM, bool exact)
        {
            if (lastDiameterM > 0f &&
                Mathf.Abs(diameterM - lastDiameterM) <= diameterChangeM)
            {
                if (!exact || lastDiameterM == diameterM) return;
            }
            coreMesh.localScale = Vector3.one * diameterM;
            lastDiameterM = diameterM;
        }
    }
}
