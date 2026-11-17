using UdonSharp;
using UnityEngine;
using VRCKatamari.BallMotor;

namespace VRCKatamari.BallPresentation
{
    /// <summary>
    /// Draws one ball's core mesh at the level's start diameter, once.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The core does not grow.</b> The original's core (the first ball) keeps its size for
    /// the whole level; the ball gets bigger because absorbed props pile up on its outside
    /// (Docs/Research/KatamariBallRules/ORIGINAL_ATTACH_LOOK.md b1, design in
    /// Docs/Design/BallLayers/Attachment/ATTACH_PLACEMENT.md 3.2). So the mesh diameter is the
    /// motor's start diameter (<c>GetStartDiameterWorldM()</c>, the size model's diameter at
    /// zero confirmed volume, the number the HUD shows when the round begins), written ONCE
    /// when the motor is ready. The physical sphere (contact, ground, absorb gate, camera) keeps
    /// growing with the ledger; absorbed props are frozen at their contact pose on that
    /// sphere's surface, so a big ball is a shell of props around the small core.
    /// </para>
    /// <para>
    /// The motor owns the ball root's position and rotation and never writes scale. This
    /// behaviour owns exactly one field: the local scale of the dedicated <c>coreMesh</c>
    /// child. It never scales the rolling root, whose scale must stay 1
    /// (Docs/02_TECHNICAL_CONTRACTS.md section 2), and never scales the attachment root,
    /// because absorbed props must not scale with the core. It never writes anything back to
    /// the motor, so a presentation setting can never move the ball.
    /// </para>
    /// <para>
    /// A motor that has not run its Start yet reports start diameter 0, which is skipped
    /// instead of written (a scale of 0 would make the ball vanish), so Start order does not
    /// matter: the first frame the motor is ready writes the scale and every later frame is
    /// one flag test. A round reset ("try again") does not change the level's start diameter,
    /// so nothing needs rewriting; a new level means a new motor Start and a new view.
    /// </para>
    /// <para>
    /// It writes no rotation: visual roll belongs to the visual-roll layer, which already runs
    /// inside the motor.
    /// </para>
    /// <para>
    /// Assembly: sits on its own GameObject by project convention, for clear ownership and a
    /// readable one-line self-check. Sharing a GameObject with another UdonSharpBehaviour is
    /// legal; the real hard constraint there is the sync mode (co-located behaviours fall back
    /// to the strictest one). <c>coreMesh</c> must be a child of <c>BallVisualRollRoot</c> whose
    /// own scale nothing else writes.
    /// </para>
    /// </remarks>
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class BallView : UdonSharpBehaviour
    {
        [SerializeField] private KatamariBallMotor motor;
        [SerializeField] private Transform coreMesh;

        // True once the start diameter has been written; the scale is never touched again.
        private bool coreDiameterWritten;

        public void Update()
        {
            if (coreDiameterWritten) return;
            if (motor == null || coreMesh == null) return;

            float startDiameterM = motor.GetStartDiameterWorldM();
            // 0 means the motor has not started (or has no usable size): wait, do not draw
            // a zero-scale ball.
            if (!(startDiameterM > 0f)) return;

            coreMesh.localScale = Vector3.one * startDiameterM;
            coreDiameterWritten = true;
        }
    }
}
