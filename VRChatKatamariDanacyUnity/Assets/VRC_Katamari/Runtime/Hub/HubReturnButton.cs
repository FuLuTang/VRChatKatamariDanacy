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
    /// Where it belongs is the level, next to the ball: the finished round already sends
    /// the player back on its own, so this button exists for the case that automatic
    /// move did not work (the plan's section 5 fallback list) and for a plain "go to the
    /// room" press while nothing is running. It is refused while a round is Running -
    /// the round controller exposes no public "end the round now", so abandoning the
    /// round from here is not possible without breaking the "Running means the player is
    /// in the level" rule; the clock ends the round and the button works again in
    /// Presenting/Result.
    /// </para>
    /// <para>
    /// Same split as <see cref="HubStartButton"/>: no state, no rule, one call into the
    /// launcher, which owns the decision and the log line.
    /// </para>
    /// </remarks>
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class HubReturnButton : UdonSharpBehaviour
    {
        [SerializeField] private HubLevelLauncher launcher;

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
                Debug.LogWarning("[hub] return button has no launcher wired; the press did"
                    + " nothing.");
                return;
            }
            launcher.PressReturnToRoom();
        }
    }
}
