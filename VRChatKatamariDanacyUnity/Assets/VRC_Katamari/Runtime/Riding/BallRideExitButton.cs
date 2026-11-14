using UdonSharp;
using UnityEngine;
using VRC.SDKBase;

namespace VRCKatamari.Riding
{
    /// <summary>
    /// The in-world "leave the ride" button: one Interact, the local player stands up.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It exists because the station is configured with Disable Station Exit on, so
    /// jumping or menu movement cannot end the ride; the only way out is a click on
    /// something. It owns no state: <see cref="BallRideStation.OnStationExited"/> is
    /// what re-enables the ball's interact and closes the ride gate, so this script
    /// cannot leave the ride half-finished by forgetting to clear a flag. The lab's
    /// exit button had to clear the driver's flags itself and therefore needed a
    /// duplicate-exit guard; this one deliberately does not.
    /// </para>
    /// <para>
    /// Object layout: its own GameObject with a collider and this behaviour, so the
    /// click has a surface of its own; one behaviour per object is the project
    /// convention (clear ownership and a readable self-check), not an engine rule -
    /// sharing is legal (Docs/Design/BallMotor/README.md, assembly constraints). It
    /// must be clickable from the seat. Interact fires only for the local client, so
    /// <c>Networking.LocalPlayer</c> is the player who pressed it.
    /// </para>
    /// <para>
    /// Pressing it while nobody is seated calls <c>ExitStation</c> with nothing to
    /// leave; that is expected to be a no-op and is unverified.
    /// </para>
    /// </remarks>
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class BallRideExitButton : UdonSharpBehaviour
    {
        [SerializeField] private VRC.SDK3.Components.VRCStation station;

        public override void Interact()
        {
            VRCPlayerApi player = Networking.LocalPlayer;
            if (player == null || station == null) return;
            station.ExitStation(player);
            Debug.Log("[ride] exit requested");
        }
    }
}
