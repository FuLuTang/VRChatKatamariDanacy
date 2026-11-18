using UdonSharp;
using UnityEngine;

namespace VRCKatamari.Story
{
    /// <summary>
    /// The "再试一次" button on the story round's result card: one press asks the round
    /// controller to reset and restart the level.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It owns no state and no rule: the only thing it does is call
    /// <see cref="StoryRoundController.RequestRetry"/>, and that method decides whether a
    /// retry is allowed (only while the result card is showing), resets the ball, the
    /// props and the clock, and leaves the card alone when it refuses. Keeping the rule
    /// in one place means the world-space and the uGUI button cannot behave differently.
    /// </para>
    /// <para>
    /// Two ways to press it, both on this one behaviour:
    /// </para>
    /// <list type="bullet">
    /// <item><description>
    /// World-space: its own GameObject with a collider and this behaviour, so the click
    /// has a surface of its own. <c>Interact</c> is the VRChat event the collider's
    /// object receives. One behaviour per object is the project convention (clear
    /// ownership and a readable self-check), not an engine rule - sharing is legal
    /// (Docs/Design/BallMotor/README.md, assembly constraints).
    /// </description></item>
    /// <item><description>
    /// uGUI: a Canvas <c>Button</c> can call <see cref="OnRetryPressed"/> through
    /// <c>UdonBehaviour.SendCustomEvent</c>. The official UI-event allowlist includes
    /// UdonBehaviour's <c>SendCustomEvent</c> (and <c>Interact</c>), so this needs no
    /// extra component: Button OnClick() -> drag the GameObject holding this behaviour
    /// -> function <c>UdonBehaviour.SendCustomEvent</c> -> argument
    /// <c>OnRetryPressed</c>. <c>SendCustomEvent</c> dispatches by string to this
    /// behaviour's public method, which is why the entry point is a public no-argument
    /// method and not a UnityEvent-visible property. That wiring pattern is externally
    /// documented, but this repository has not built or pressed a uGUI button yet.
    /// </description></item>
    /// </list>
    /// <para>
    /// A missing controller link is logged once per press instead of throwing: a
    /// mistyped scene wire shows up as a button that does nothing, which is visible in
    /// the console, not as a broken world.
    /// </para>
    /// </remarks>
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class StoryRetryButton : UdonSharpBehaviour
    {
        [SerializeField] private StoryRoundController controller;
        [SerializeField] private VRCKatamari.Hub.HubLevelLauncher launcher;

        /// <summary>World-space press: the retry button's collider was interacted with.</summary>
        public override void Interact()
        {
            Press();
        }

        /// <summary>
        /// uGUI entry point: a Canvas Button calls this through
        /// <c>UdonBehaviour.SendCustomEvent("OnRetryPressed")</c>. Public on purpose -
        /// string dispatch can only reach public names.
        /// </summary>
        public void OnRetryPressed()
        {
            Press();
        }

        private void Press()
        {
            if (controller == null)
            {
                Debug.LogWarning("[StoryRetryButton] controller is not wired; the button does"
                    + " nothing.");
                return;
            }
            // The dash's trigger also raises Interact; while it charges/bursts the same press
            // must not retry (owner 2026-11-18, RideDashInteractMath).
            if (controller.ShouldIgnoreDashInteract()) return;
            if (launcher != null) launcher.PressRetryCurrentLevel();
            else controller.RequestRetry();
        }
    }
}
