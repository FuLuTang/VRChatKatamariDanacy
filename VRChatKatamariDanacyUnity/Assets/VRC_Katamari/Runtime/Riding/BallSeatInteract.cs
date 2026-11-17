using UdonSharp;
using UnityEngine;
using VRCKatamari.BallMotor;

namespace VRCKatamari.Riding
{
    /// <summary>
    /// The thing the player clicks to sit down: a trigger sphere that follows the ball and
    /// grows with it, so "click the ball" is literally what works.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Why this is its own object. Interact is delivered to the behaviour on the GameObject
    /// of the collider the crosshair hit, and the seat's own collider (on the VRCStation
    /// object) hangs on the follow frame, at the rider's eye point rather than on the
    /// ball. The click target therefore lives under the ball's physics root, which the motor
    /// moves every frame, and forwards the click to <see cref="BallRideStation"/>.
    /// </para>
    /// <para>
    /// The object carries a unit sphere (radius 0.5 m) and is scaled to the wanted
    /// diameter, so the radius follows the ball with a transform write instead of a
    /// collider property. It is a trigger: it must not push the player or the props, and
    /// the player may stand inside it. Local state only (<c>BehaviourSyncMode.None</c>).
    /// </para>
    /// <para>
    /// While the local player is seated the station turns the prompt off through
    /// <see cref="SetInteractable"/>; the sphere stays so it is ready the moment they leave.
    /// </para>
    /// </remarks>
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class BallSeatInteract : UdonSharpBehaviour
    {
        // The seat this click boards.
        [SerializeField] private BallRideStation ride;

        // The ball whose collision radius sizes the sphere. Optional: without it the sphere
        // keeps the scale the scene gave it.
        [SerializeField] private KatamariBallMotor motor;

        private float lastRadius = -1f;

        public override void Interact()
        {
            if (ride == null)
            {
                Debug.LogWarning("[ride] ball click ignored: BallSeatInteract.ride is not wired");
                return;
            }
            ride.TrySeatLocalPlayer();
        }

        /// <summary>Shows or hides the interaction prompt (off while seated).</summary>
        public void SetInteractable(bool interactable)
        {
            DisableInteractive = !interactable;
        }

        private void Update()
        {
            if (motor == null) return;
            float radius = RideViewMath.SeatTriggerRadius(motor.GetCollisionRadiusWorldM());
            // Written only on a change: the radius moves in steps when the ball absorbs
            // something, and a scale write per frame would be wasted work.
            if (radius == lastRadius) return;
            lastRadius = radius;
            float diameter = radius * 2f;
            transform.localScale = new Vector3(diameter, diameter, diameter);
        }
    }
}
