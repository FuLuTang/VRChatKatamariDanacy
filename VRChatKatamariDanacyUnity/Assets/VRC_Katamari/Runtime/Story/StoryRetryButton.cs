using UdonSharp;
using UnityEngine;

namespace VRCKatamari.Story
{
    /// <summary>
    /// The "再来一次" button on the story round's result card: one press asks for the level
    /// to be reset and restarted.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A uGUI Button on the result card prefab (Docs/Design/StoryMode/HUB_PANEL_PREFAB.md,
    /// "结算卡"): its persistent <c>onClick</c> calls <c>UdonBehaviour.SendCustomEvent("Press")</c>
    /// on this object's backing UdonBehaviour, the same wiring as the hub wall panels.
    /// String dispatch only reaches public methods, hence <see cref="Press"/> is public and
    /// takes no argument. There is no collider and no <c>Interact</c>: the canvas's own
    /// BoxCollider + VRCUiShape is the hit box for VRChat's laser pointer and the mouse.
    /// </para>
    /// <para>
    /// It owns no state and no rule. With a hub launcher wired it calls
    /// <c>HubLevelLauncher.PressRetryCurrentLevel</c> (which cancels a pending automatic
    /// return first); in a lab without a hub it calls
    /// <see cref="StoryRoundController.RequestRetry"/> directly. Both refuse outside
    /// <c>StateResult</c>, and the button is only visible in Result, while the round gate is
    /// closed - so no dash can be charging when it is pressed (the bell's freeze closes the
    /// gate and cancels the dash), and the dash/trigger guard of the ride panels is not
    /// needed here.
    /// </para>
    /// </remarks>
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class StoryRetryButton : UdonSharpBehaviour
    {
        /// <summary>The button's word on the card (in Tools/hubart/hub_panel_charset.txt).</summary>
        public const string Label = "再来一次";

        [SerializeField] private StoryRoundController controller;
        [SerializeField] private VRCKatamari.Hub.HubLevelLauncher launcher;

        /// <summary>Button.onClick target: <c>SendCustomEvent("Press")</c>.</summary>
        public void Press()
        {
            if (controller == null)
            {
                Debug.LogWarning("[StoryRetryButton] controller is not wired; the button does"
                    + " nothing.");
                return;
            }
            if (launcher != null) launcher.PressRetryCurrentLevel();
            else controller.RequestRetry();
        }
    }
}
