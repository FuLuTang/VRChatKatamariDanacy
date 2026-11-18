using NUnit.Framework;
using VRCKatamari.BallContact;
using VRCKatamari.BallMotor;

namespace VRCKatamari.Tests
{
    /// <summary>
    /// The dash rule (CONTACT_MODEL_V2 §3.8, owner 2026-11-18): hold to charge, spin in place
    /// from the third gacha window on, release only fires a full charge, one burst per charge,
    /// a cooldown, and the original's boost energy and huff.
    /// </summary>
    public class DashMathTests
    {
        private const float Step = 0.02f;  // the motor's 50 Hz fixed step

        private static float[] FreshState()
        {
            float[] state = new float[DashMath.StateLength];
            Assert.IsTrue(DashMath.TryReset(state));
            return state;
        }

        private static void Advance(float[] state, float seconds)
        {
            int steps = (int)System.Math.Round(seconds / Step);
            for (int i = 0; i < steps; i++)
            {
                Assert.IsTrue(DashMath.TryStep(state, Step));
            }
        }

        [Test]
        public void TheTiersAreTheOriginalGachaCountsInGachaWindows()
        {
            // 5/6/7/8 required gachas below 50 cm, up to 5 m, up to 10 m, above (REROLL notes).
            Assert.AreEqual(5, DashMath.ChargeGachasFor(0.3f));
            Assert.AreEqual(5, DashMath.ChargeGachasFor(0.49f));
            Assert.AreEqual(6, DashMath.ChargeGachasFor(0.5f));
            Assert.AreEqual(6, DashMath.ChargeGachasFor(4.99f));
            Assert.AreEqual(7, DashMath.ChargeGachasFor(5f));
            Assert.AreEqual(7, DashMath.ChargeGachasFor(9.99f));
            Assert.AreEqual(8, DashMath.ChargeGachasFor(10f));
            Assert.AreEqual(5, DashMath.ChargeGachasFor(float.NaN), "an unusable size uses the smallest tier");

            // One 0.4 s gacha window per required gacha: 2.0 / 2.4 / 2.8 / 3.2 s.
            Assert.AreEqual(2.0f, DashMath.ChargeSecondsFor(0.3f), 1e-6f);
            Assert.AreEqual(2.4f, DashMath.ChargeSecondsFor(0.5f), 1e-6f);
            Assert.AreEqual(2.8f, DashMath.ChargeSecondsFor(5f), 1e-6f);
            Assert.AreEqual(3.2f, DashMath.ChargeSecondsFor(10f), 1e-6f);
        }

        [Test]
        public void TheSpinStartsAtThreeGachaWindows()
        {
            float[] state = FreshState();
            Assert.IsTrue(DashMath.TryBeginCharge(state));
            Assert.IsFalse(DashMath.IsSpinning(state), "no spin before the third gacha");

            Advance(state, 1.0f);
            Assert.IsFalse(DashMath.IsSpinning(state), "still under 1.2 s of charge");

            Advance(state, 0.4f);
            Assert.IsTrue(DashMath.IsSpinning(state), "spin from gacha count 3 (1.2 s) on");
            Assert.IsTrue(DashMath.IsCharging(state), "spinning is still charging");
        }

        [Test]
        public void AShortReleaseDoesNotFireAndCostsNoCooldown()
        {
            float[] state = FreshState();
            Assert.IsTrue(DashMath.TryBeginCharge(state));
            Advance(state, 1.0f);             // under the 2.0 s tier for a small ball
            Assert.AreEqual(DashMath.ReleaseNotFull, DashMath.ReleaseCharge(state, 0.3f));
            Assert.IsFalse(DashMath.IsDashing(state));
            Assert.AreEqual(1f, DashMath.SpeedScale(state));
            Assert.IsTrue(DashMath.TryBeginCharge(state), "a short charge leaves no cooldown");
        }

        [Test]
        public void AFullReleaseFiresTheBurstAboutDashSeconds()
        {
            float[] state = FreshState();
            Assert.IsTrue(DashMath.TryBeginCharge(state));
            Advance(state, DashMath.ChargeSecondsFor(0.3f));
            Assert.AreEqual(DashMath.ReleaseDash, DashMath.ReleaseCharge(state, 0.3f));
            Assert.IsTrue(DashMath.IsDashing(state));
            Assert.AreEqual(ContactTuning.DashSpeedScale, DashMath.SpeedScale(state), 0f);
            Assert.AreEqual(1.6f, DashMath.SpeedScale(state), 0f, "CONTACT_MODEL_V2 §3.8: 1.6x");

            int steps = 0;
            while (DashMath.IsDashing(state) && steps < 200)
            {
                steps++;
                Assert.IsTrue(DashMath.TryStep(state, Step));
            }
            Assert.IsFalse(DashMath.IsDashing(state));
            Assert.LessOrEqual(System.Math.Abs(steps * Step - ContactTuning.DashSeconds), Step,
                "the burst is never more than one fixed step off DashSeconds");
        }

        [Test]
        public void AChargeIsRefusedWhileDashingCoolingDownOrHuffing()
        {
            float[] state = FreshState();
            Assert.IsTrue(DashMath.TryBeginCharge(state));
            Advance(state, DashMath.ChargeSecondsFor(0.3f));
            Assert.AreEqual(DashMath.ReleaseDash, DashMath.ReleaseCharge(state, 0.3f));
            Assert.IsFalse(DashMath.TryBeginCharge(state), "no second charge during the burst");

            Advance(state, ContactTuning.DashSeconds + 0.1f);
            Assert.IsFalse(DashMath.IsDashing(state));
            Assert.IsFalse(DashMath.TryBeginCharge(state), "no charge during the cooldown");

            Advance(state, ContactTuning.DashCooldownS);
            Assert.IsTrue(DashMath.TryBeginCharge(state), "ready again once the cooldown expired");
        }

        [Test]
        public void EnergyDrainsFromTheThirdGachaWindowThroughTheBurst()
        {
            float[] state = FreshState();
            float capacity = DashMath.EnergyCapacitySeconds;
            Assert.AreEqual(8f, capacity, 1e-6f, "240 frames at 30 Hz");

            Assert.IsTrue(DashMath.TryBeginCharge(state));
            Advance(state, 0.8f);
            Assert.AreEqual(capacity, DashMath.EnergySeconds(state), 1e-4f,
                "the first two gacha windows do not drain energy");

            Advance(state, 1.2f);             // charge 2.0 s total
            Assert.Less(DashMath.EnergySeconds(state), capacity - 0.6f,
                "the tail of the charge drains energy");
            Assert.Greater(DashMath.EnergySeconds(state), capacity - 1.1f);

            Assert.AreEqual(DashMath.ReleaseDash, DashMath.ReleaseCharge(state, 0.3f));
            Advance(state, ContactTuning.DashSeconds + DashMath.GachaWindowSeconds + 0.1f);
            // 0.8 s of the charge + 0.45 s of the burst = 1.25 s of energy; the gacha tail
            // (0.4 s) ended inside the burst, so it adds nothing more.
            Assert.Less(DashMath.EnergySeconds(state), capacity - 1.1f,
                "the burst and the gacha tail keep draining");
            Assert.Greater(DashMath.EnergySeconds(state), capacity - 1.4f);
        }

        [Test]
        public void TheHuffBeginsAtZeroEnergyAndRefusesChargesUntilItEnds()
        {
            float[] state = FreshState();
            state[DashMath.StateEnergySeconds] = 0.2f;

            Assert.IsTrue(DashMath.TryBeginCharge(state));
            Advance(state, 1.5f);             // spin threshold 1.2 s, then 0.2 s of drain
            Assert.IsTrue(DashMath.IsHuffed(state));
            Assert.IsFalse(DashMath.IsCharging(state), "the huff drops the charge");
            Assert.IsFalse(DashMath.TryBeginCharge(state), "huffing: no boost inputs");

            Advance(state, DashMath.HuffSeconds);
            Assert.IsFalse(DashMath.IsHuffed(state));
            Assert.AreEqual(DashMath.EnergyCapacitySeconds, DashMath.EnergySeconds(state), 1e-4f,
                "the original refills the energy when the huff ends");
            Assert.IsTrue(DashMath.TryBeginCharge(state));
        }

        [Test]
        public void TheRechargeArrivesAfterHundredIdleFrames()
        {
            float[] state = FreshState();
            state[DashMath.StateEnergySeconds] = 4f;

            Advance(state, 3.3f);             // just under 100 frames (3.333 s)
            Assert.AreEqual(4f, DashMath.EnergySeconds(state), 1e-4f);

            Advance(state, 0.2f);             // crosses the threshold: one recharge event
            Assert.AreEqual(4f + DashMath.RechargeEnergySeconds, DashMath.EnergySeconds(state), 1e-3f);
            Assert.AreEqual(0.6f, DashMath.RechargeEnergySeconds, 1e-6f, "18 frames at 30 Hz");
        }

        [Test]
        public void TheRechargeNeverExceedsTheCapacity()
        {
            float[] state = FreshState();
            state[DashMath.StateEnergySeconds] = DashMath.EnergyCapacitySeconds - 0.1f;
            Advance(state, 4f);
            Assert.AreEqual(DashMath.EnergyCapacitySeconds, DashMath.EnergySeconds(state), 1e-4f);
        }

        [Test]
        public void CancelDropsTheChargeWithoutAFire()
        {
            float[] state = FreshState();
            Assert.IsTrue(DashMath.TryBeginCharge(state));
            Advance(state, 1.5f);
            DashMath.CancelCharge(state);
            Assert.IsFalse(DashMath.IsCharging(state));
            Assert.AreEqual(0f, DashMath.ChargeSeconds(state));
            Assert.AreEqual(DashMath.ReleaseNone, DashMath.ReleaseCharge(state, 0.3f));
            Assert.IsFalse(DashMath.IsDashing(state));
        }

        [Test]
        public void ReleaseWithoutAChargeIsNone()
        {
            float[] state = FreshState();
            Assert.AreEqual(DashMath.ReleaseNone, DashMath.ReleaseCharge(state, 0.3f));
        }

        [Test]
        public void ResetClearsEverythingAndRefillsTheEnergy()
        {
            float[] state = FreshState();
            Assert.IsTrue(DashMath.TryBeginCharge(state));
            Advance(state, DashMath.ChargeSecondsFor(0.3f));
            Assert.AreEqual(DashMath.ReleaseDash, DashMath.ReleaseCharge(state, 0.3f));
            Assert.IsTrue(DashMath.TryReset(state));
            Assert.IsFalse(DashMath.IsCharging(state));
            Assert.IsFalse(DashMath.IsDashing(state));
            Assert.IsFalse(DashMath.IsHuffed(state));
            Assert.AreEqual(0f, DashMath.ChargeSeconds(state));
            Assert.AreEqual(DashMath.EnergyCapacitySeconds, DashMath.EnergySeconds(state), 1e-6f);
            Assert.AreEqual(1f, DashMath.SpeedScale(state));
        }

        [Test]
        public void ABadStateArrayOrDeltaIsRefused()
        {
            float[] state = FreshState();
            Assert.IsTrue(DashMath.TryBeginCharge(state));
            Assert.IsFalse(DashMath.TryStep(state, float.NaN));
            Assert.IsFalse(DashMath.TryStep(state, -0.02f));
            Assert.IsTrue(DashMath.IsCharging(state), "a refused step changes nothing");

            Assert.IsFalse(DashMath.TryStep(null, Step));
            Assert.IsFalse(DashMath.TryStep(new float[3], Step));
            Assert.IsFalse(DashMath.TryBeginCharge(null));
            Assert.IsFalse(DashMath.TryBeginCharge(new float[3]));
            Assert.IsFalse(DashMath.TryReset(null));
            Assert.IsFalse(DashMath.IsCharging(null));
            Assert.AreEqual(1f, DashMath.SpeedScale(null), 0f, "an unusable state never speeds the ball up");
            Assert.AreEqual(DashMath.ReleaseNone, DashMath.ReleaseCharge(null, 0.3f));
        }

        [Test]
        public void ANonFiniteStateIsRefused()
        {
            float[] state = FreshState();
            state[DashMath.StateChargeSeconds] = float.NaN;
            Assert.IsFalse(DashMath.TryBeginCharge(state));
            Assert.IsFalse(DashMath.TryStep(state, Step));
        }
    }
}
