using NUnit.Framework;
using VRCKatamari.Riding;

namespace VRCKatamari.Tests
{
    /// <summary>
    /// The dash/Interact conflict rule (owner 2026-11-18): while the rider sits on the ball, a
    /// boarding or exit-panel Interact press is dropped while the dash charges or bursts, because
    /// VR's trigger raises both events from one press. Outside a dash nothing changes.
    /// </summary>
    public class RideDashInteractTests
    {
        [Test]
        public void AChargeWhileSeatedDropsThePanelPress()
        {
            Assert.IsTrue(RideDashInteractMath.ShouldIgnoreInteract(true, true, false));
        }

        [Test]
        public void ABurstWhileSeatedDropsThePanelPress()
        {
            Assert.IsTrue(RideDashInteractMath.ShouldIgnoreInteract(true, false, true));
            Assert.IsTrue(RideDashInteractMath.ShouldIgnoreInteract(true, true, true));
        }

        [Test]
        public void AnIdleDashLeavesThePanelAlone()
        {
            Assert.IsFalse(RideDashInteractMath.ShouldIgnoreInteract(true, false, false));
        }

        [Test]
        public void AStandingPlayerIsNeverAffected()
        {
            // Defensive: the adapters cannot start a charge outside the ride gate, so this
            // combination should not happen - but if it ever does, a standing player's panel
            // press must not be swallowed.
            Assert.IsFalse(RideDashInteractMath.ShouldIgnoreInteract(false, true, false));
            Assert.IsFalse(RideDashInteractMath.ShouldIgnoreInteract(false, false, true));
        }
    }
}
