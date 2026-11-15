using UdonSharp;
using UnityEngine;

namespace VRCKatamari.Hub
{
    /// <summary>
    /// The room's "start" button: one press asks the hub to send the local player into
    /// the level this button names.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It owns no state and no rule. Everything that decides whether a press may start a
    /// transfer - the round's state, a transfer already in flight, the cooldown, the
    /// level index - is <see cref="HubLevelLauncher"/>'s, which asks
    /// <see cref="HubLoopMath"/>. Two buttons therefore cannot disagree, and this file
    /// stays small enough to read at a glance while wiring a scene.
    /// </para>
    /// <para>
    /// One behaviour per object with a collider is the plan's first-pass choice
    /// (HUB_LOOP_PLAN section 3.1: the scene has no <c>EventSystem</c>, and the in-world
    /// object + collider + <c>Interact</c> path has precedent in
    /// <c>BallRideExitButton</c>). <c>Interact</c> is delivered to the behaviour on the
    /// collider's object, so the collider and this behaviour share the button object.
    /// <see cref="Press"/> is public and no-argument so a future uGUI <c>Button</c> can
    /// reach it through <c>UdonBehaviour.SendCustomEvent("Press")</c>, the same pattern
    /// <c>StoryRetryButton.OnRetryPressed</c> documents; no uGUI is built here.
    /// </para>
    /// <para>
    /// <c>levelIndex</c> is the button's own data, which is what makes "one row per
    /// level" a scene change instead of a code change: a second level is a second button
    /// with index 1 and a second entry transform. Nothing here reads the index's meaning,
    /// so nothing here changes when a level is added.
    /// </para>
    /// </remarks>
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class HubStartButton : UdonSharpBehaviour
    {
        [SerializeField] private HubLevelLauncher launcher;

        // The level this button asks for: the launcher's levelEntries index.
        [SerializeField] private int levelIndex = 0;

        /// <summary>World-space press: this button's collider was interacted with.</summary>
        public override void Interact()
        {
            Press();
        }

        /// <summary>uGUI seam: <c>SendCustomEvent("Press")</c> reaches this name.</summary>
        public void Press()
        {
            if (launcher == null)
            {
                Debug.LogWarning("[hub] start button has no launcher wired; the press did"
                    + " nothing.");
                return;
            }
            launcher.PressStart(levelIndex);
        }
    }
}
