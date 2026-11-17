using UdonSharp;
using UnityEngine;
using VRCKatamari.BallMotor;
using VRCKatamari.Core;

namespace VRCKatamari.BallPresentation
{
    /// <summary>Draws the core at the current collision sphere diameter, without smoothing.</summary>
    /// <remarks>
    /// Owns only the dedicated CoreMesh child's local scale. The unit-diameter mesh (or
    /// normalized skin child) follows the collision sphere used by the pickup contact gate.
    /// The visual-roll root and attachment root stay at unit scale: attached props keep
    /// their captured pose and original size. All CoreMesh ancestors must have unit scale.
    /// Position and rotation remain owned by the motor's visual-roll layer.
    /// </remarks>
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class BallView : UdonSharpBehaviour
    {
        [SerializeField] private KatamariBallMotor motor;
        [SerializeField] private Transform coreMesh;

        private float lastDiameterM;

        public void Update()
        {
            if (motor == null || coreMesh == null) return;

            float diameterM = 2f * motor.GetCollisionRadiusWorldM();
            // Wait for a usable motor size; never hide the mesh with a zero/invalid scale.
            if (!KatamariMath.IsFiniteFloat(diameterM) || diameterM <= 0f) return;
            if (diameterM == lastDiameterM) return;

            coreMesh.localScale = Vector3.one * diameterM;
            lastDiameterM = diameterM;
        }
    }
}
