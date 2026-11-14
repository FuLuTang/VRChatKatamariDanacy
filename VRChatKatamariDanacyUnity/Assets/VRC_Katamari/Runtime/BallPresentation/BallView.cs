using UdonSharp;
using UnityEngine;
using VRCKatamari.BallMotor;
using VRCKatamari.Core;

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
    /// no size state of its own beyond the last value it wrote, and a re-scale only
    /// happens when the diameter moved by more than 1e-4 m.
    /// </para>
    /// <para>
    /// It writes no rotation: visual roll belongs to the visual-roll layer, which
    /// already runs inside the motor. A motor that has not run its Start yet reads
    /// diameter 0, which is skipped instead of written, so Start order does not
    /// matter here.
    /// </para>
    /// <para>
    /// Assembly: needs its own GameObject (two UdonSharpBehaviours on one object
    /// means UdonSharp silently disables one of them), and <c>coreMesh</c> must be a
    /// child of <c>BallVisualRollRoot</c> whose own scale nothing else writes.
    /// </para>
    /// </remarks>
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class BallView : UdonSharpBehaviour
    {
        // A re-scale below this is not visible and would only dirty the transform
        // every frame.
        private const float diameterChangeM = 0.0001f;

        [SerializeField] private KatamariBallMotor motor;
        [SerializeField] private Transform coreMesh;

        // The last diameter actually written; a motor that never started leaves it
        // negative, which is also how the first write gets through.
        private float lastDiameterM = -1f;

        public void Update()
        {
            if (motor == null || coreMesh == null) return;
            float diameterM = motor.GetSemanticDiameterWorldM();
            if (!KatamariMath.IsFiniteFloat(diameterM)) return;
            if (diameterM <= 0f) return;
            if (lastDiameterM > 0f &&
                Mathf.Abs(diameterM - lastDiameterM) <= diameterChangeM) return;
            coreMesh.localScale = Vector3.one * diameterM;
            lastDiameterM = diameterM;
        }
    }
}
