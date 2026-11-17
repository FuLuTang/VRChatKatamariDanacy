using UdonSharp;
using UnityEngine;
using UnityEngine.UI;

namespace VRCKatamari.Riding
{
    /// <summary>
    /// The rider's "get off" button, on the panel that hangs in their view: press twice
    /// within a few seconds and the ride ends.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Why twice. The station keeps Disable Station Exit on so that movement and jumping
    /// cannot stand the rider up mid-ride, and a single click on a button that sits in the
    /// view is exactly the accident that setting guards against. The first press arms the
    /// button and changes its label; a second press inside <c>confirmSeconds</c> asks
    /// <see cref="BallRideStation.RequestExit"/> to leave. A press after the window starts
    /// over. The other route out, holding the jump input, needs no button.
    /// </para>
    /// <para>
    /// It owns no ride state: <see cref="BallRideStation.OnStationExited"/> still clears the
    /// seated flag, the ride gate and the prompts, and <c>RequestExit</c> places the exit
    /// location on walkable ground first. Object layout: its own GameObject under the
    /// rider's panel with a collider and this behaviour (Interact is delivered to the
    /// behaviour on the collider's object). Interact fires only for the local client.
    /// Local state only (<c>BehaviourSyncMode.None</c>).
    /// </para>
    /// </remarks>
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class BallRideExitButton : UdonSharpBehaviour
    {
        [SerializeField] private BallRideStation ride;

        // Optional. The button's face; it shows the armed state.
        [SerializeField] private Text label;

        // Seconds the second press may follow the first.
        [SerializeField] private float confirmSeconds = 3f;

        private const string IdleLabel = "下车（点两次）";
        private const string ArmedLabel = "再点一次确认下车";

        private bool armed;
        private float armedAt;

        public override void Interact()
        {
            if (ride == null)
            {
                Debug.LogWarning("[ride] exit press ignored: BallRideExitButton.ride is not wired");
                return;
            }
            if (!ride.IsLocalPlayerSeated()) return;
            if (armed && Time.time - armedAt <= confirmSeconds)
            {
                Disarm();
                ride.RequestExit();
                return;
            }
            armed = true;
            armedAt = Time.time;
            WriteLabel(ArmedLabel);
        }

        private void Update()
        {
            if (!armed) return;
            if (Time.time - armedAt <= confirmSeconds) return;
            Disarm();
        }

        private void Disarm()
        {
            armed = false;
            WriteLabel(IdleLabel);
        }

        private void WriteLabel(string text)
        {
            if (label == null) return;
            label.text = text;
        }
    }
}
