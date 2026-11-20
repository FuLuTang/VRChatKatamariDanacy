using UdonSharp;
using UnityEngine;
using VRCKatamari.BallMotor;
using VRCKatamari.Core;

namespace VRCKatamari.BallPresentation
{
    /// <summary>
    /// Draws the core at the current collision sphere diameter, without smoothing, wearing one
    /// of the assembled original core skins.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Owns only the dedicated CoreMesh child's local scale. The unit-diameter mesh (or
    /// normalized skin children) follows the collision sphere used by the pickup contact gate.
    /// The visual-roll root and attachment root stay at unit scale: attached props keep
    /// their captured pose and original size. All CoreMesh ancestors must have unit scale.
    /// Position and rotation remain owned by the motor's visual-roll layer.
    /// </para>
    /// <para>
    /// Skins. The core skin assembler fills <see cref="skinNumbers"/> (original core numbers,
    /// ascending) and <see cref="skinRenderers"/> (one normalized display child per number,
    /// same order). <see cref="SetSkin"/> only flips those renderers' <c>enabled</c>, so exactly
    /// one draws; scale, collision and roll are untouched. Which skin is chosen and saved is
    /// the hub settings' (<c>HubSettingsHost</c>); this behaviour is local display only and
    /// syncs nothing.
    /// </para>
    /// </remarks>
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class BallView : UdonSharpBehaviour
    {
        [SerializeField] private KatamariBallMotor motor;
        [SerializeField] private Transform coreMesh;

        [Tooltip("Original core numbers of the assembled skins, ascending. Written by the core skin assembler.")]
        [SerializeField] private int[] skinNumbers;

        [Tooltip("The display renderer of each skin in skinNumbers, same order. Written by the core skin assembler.")]
        [SerializeField] private MeshRenderer[] skinRenderers;

        private float lastDiameterM;
        private int currentSkin;

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

        /// <summary>The skin numbers this ball can wear (empty when no skin was assembled).</summary>
        public int[] GetSkinNumbers()
        {
            if (skinNumbers == null) return new int[0];
            return skinNumbers;
        }

        /// <summary>The worn skin's number, or 0 before the first <see cref="SetSkin"/>.</summary>
        public int GetCurrentSkin() { return currentSkin; }

        /// <summary>
        /// Shows the skin with original core number <paramref name="number"/> and hides the
        /// others. An unknown number (or no skin table) changes nothing.
        /// </summary>
        public void SetSkin(int number)
        {
            if (skinNumbers == null || skinRenderers == null) return;
            int index = BallSkinMath.IndexOf(skinNumbers, number);
            if (index < 0 || index >= skinRenderers.Length || skinRenderers[index] == null) return;
            for (int i = 0; i < skinRenderers.Length; i++)
            {
                MeshRenderer renderer = skinRenderers[i];
                if (renderer == null) continue;
                bool on = i == index;
                if (renderer.enabled != on) renderer.enabled = on;
            }
            currentSkin = number;
        }
    }
}
