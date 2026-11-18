using UdonSharp;
using UnityEngine;

namespace VRCKatamari.Hub
{
    /// <summary>
    /// One level card on the hub's levels board: a click on the card asks the hub to send the
    /// local player into the level this card names.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The card is a uGUI <c>Button</c> on a world-space Canvas with <c>VRC_UiShape</c>
    /// (VRChat's interactable-canvas setup, creators.vrchat.com/worlds/components/vrc_uishape).
    /// The Button's persistent <c>onClick</c> calls <c>UdonBehaviour.SendCustomEvent("Press")</c>
    /// on this behaviour's backing UdonBehaviour, set in the panel prefab in the editor
    /// (Docs/Design/StoryMode/HUB_PANEL_PREFAB.md; runtime <c>AddListener</c> is not available
    /// to Udon) and read back by the hub assembler. <c>SendCustomEvent</c> only
    /// reaches public methods, hence <see cref="Press"/> is public and takes no argument.
    /// </para>
    /// <para>
    /// It owns no state and no rule. Whether a press may start a transfer - the round's state,
    /// a transfer already in flight, the cooldown, the level index - is
    /// <see cref="HubLevelLauncher"/>'s. <c>levelIndex</c> is this card's own data (the
    /// launcher's <c>levelEntries</c> index), so a new level is a new card, not new code.
    /// </para>
    /// </remarks>
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class HubStartButton : UdonSharpBehaviour
    {
        [SerializeField] private HubLevelLauncher launcher;

        // The level this card asks for: the launcher's levelEntries index.
        [SerializeField] private int levelIndex = 0;

        /// <summary>Button.onClick target: <c>SendCustomEvent("Press")</c>.</summary>
        public void Press()
        {
            if (launcher == null)
            {
                Debug.LogWarning("[hub] level card has no launcher wired; the press did nothing.");
                return;
            }
            Debug.Log("[hub-panel] level card " + levelIndex + " pressed");
            launcher.PressStart(levelIndex);
        }
    }
}
