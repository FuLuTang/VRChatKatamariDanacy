using UdonSharp;
using UnityEngine;

namespace VRCKatamari.Hub
{
    /// <summary>
    /// The "back to the room" button: one press asks the hub to send the local player
    /// to the room point, leaving the ball's seat first if they are in it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two places use it, with one entry point, <see cref="Press"/>:
    /// </para>
    /// <list type="bullet">
    /// <item><description>The result card's "回大厅" (the result card prefab,
    /// Docs/Design/StoryMode/HUB_PANEL_PREFAB.md): a uGUI Button whose persistent onClick is
    /// <c>UdonBehaviour.SendCustomEvent("Press")</c>; that object has no collider, so
    /// <see cref="Interact"/> is never raised there.</description></item>
    /// <item><description>Each level's <c>HubReturnButton_&lt;id&gt;</c>, a cube standing next
    /// to the level's entry (HubAssembler.AssembleReturnButtons): the way out of a free roam
    /// level, which has no result card. It is a world object, not a panel, so it keeps the
    /// collider and <see cref="Interact"/>.</description></item>
    /// </list>
    /// <para>
    /// It is refused while a round is Running unless the level allows giving up; the
    /// launcher owns that decision and the log line. Same split as
    /// <see cref="HubStartButton"/>: no state, no rule, one call into the launcher.
    /// </para>
    /// </remarks>
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class HubReturnButton : UdonSharpBehaviour
    {
        /// <summary>The button's word (card label and cube prompt; in Tools/hubart/hub_panel_charset.txt).</summary>
        public const string Label = "回大厅";

        [SerializeField] private HubLevelLauncher launcher;

        /// <summary>The level's return cube: its collider was interacted with.</summary>
        public override void Interact()
        {
            Press();
        }

        /// <summary>Button.onClick target: <c>SendCustomEvent("Press")</c>.</summary>
        public void Press()
        {
            if (launcher == null)
            {
                Debug.LogWarning("[hub] return button has no launcher wired; the press did"
                    + " nothing.");
                return;
            }
            launcher.PressReturnToRoom();
        }
    }
}
