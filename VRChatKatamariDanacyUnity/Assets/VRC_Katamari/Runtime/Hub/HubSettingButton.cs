using UdonSharp;
using UnityEngine;

namespace VRCKatamari.Hub
{
    /// <summary>
    /// One arrow of one setting row on the hub's settings board: a click steps that setting by
    /// <c>delta</c> in <see cref="HubSettingsHost"/>.
    /// </summary>
    /// <remarks>
    /// Same pattern as <see cref="HubStartButton"/>: the arrow is a uGUI <c>Button</c> whose
    /// persistent <c>onClick</c> calls <c>UdonBehaviour.SendCustomEvent("Press")</c> on this
    /// behaviour (set in the panel prefab, read back by the hub assembler). It owns no state.
    /// </remarks>
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class HubSettingButton : UdonSharpBehaviour
    {
        [SerializeField] private HubSettingsHost settings;

        [Tooltip("HubSettingsMath id of the row this arrow belongs to.")]
        [SerializeField] private int settingId;

        [Tooltip("+1 for the right arrow, -1 for the left arrow.")]
        [SerializeField] private int delta = 1;

        /// <summary>Button.onClick target: <c>SendCustomEvent("Press")</c>.</summary>
        public void Press()
        {
            if (settings == null)
            {
                Debug.LogWarning("[hub-settings] setting button has no settings host wired");
                return;
            }
            settings.StepSetting(settingId, delta);
        }
    }
}
