using NUnit.Framework;
using VRCKatamari.Input;

namespace VRCKatamari.Tests
{
    public class DashCueTests
    {
        [Test]
        public void PhaseChangesMapToCues()
        {
            Assert.AreEqual(DashCueMath.CueChargeStart, DashCueMath.CueFor(DashCueMath.PhaseIdle, DashCueMath.PhaseCharging));
            Assert.AreEqual(DashCueMath.CueSpin, DashCueMath.CueFor(DashCueMath.PhaseCharging, DashCueMath.PhaseSpinning));
            Assert.AreEqual(DashCueMath.CueLaunch, DashCueMath.CueFor(DashCueMath.PhaseSpinning, DashCueMath.PhaseDashing));
            Assert.AreEqual(DashCueMath.CueLaunch, DashCueMath.CueFor(DashCueMath.PhaseCharging, DashCueMath.PhaseDashing));
            Assert.AreEqual(DashCueMath.CueStop, DashCueMath.CueFor(DashCueMath.PhaseCharging, DashCueMath.PhaseIdle));
            Assert.AreEqual(DashCueMath.CueStop, DashCueMath.CueFor(DashCueMath.PhaseSpinning, DashCueMath.PhaseIdle));
        }

        [Test]
        public void NoChangeOrDashEndingMakesNoCue()
        {
            Assert.AreEqual(DashCueMath.CueNone, DashCueMath.CueFor(DashCueMath.PhaseCharging, DashCueMath.PhaseCharging));
            Assert.AreEqual(DashCueMath.CueNone, DashCueMath.CueFor(DashCueMath.PhaseDashing, DashCueMath.PhaseIdle));
            Assert.AreEqual(DashCueMath.CueNone, DashCueMath.CueFor(DashCueMath.PhaseIdle, DashCueMath.PhaseDashing));
        }

        [Test]
        public void PhaseOfPrefersChargeOverDash()
        {
            Assert.AreEqual(DashCueMath.PhaseSpinning, DashCueMath.PhaseOf(true, true, false));
            Assert.AreEqual(DashCueMath.PhaseDashing, DashCueMath.PhaseOf(false, false, true));
            Assert.AreEqual(DashCueMath.PhaseIdle, DashCueMath.PhaseOf(false, false, false));
        }

        [Test]
        public void HapticsGrowWithStageAndFollowTheSetting()
        {
            float a = DashCueMath.HapticAmplitude(DashCueMath.CueChargeStart, 1f);
            float b = DashCueMath.HapticAmplitude(DashCueMath.CueSpin, 1f);
            float c = DashCueMath.HapticAmplitude(DashCueMath.CueLaunch, 1f);
            Assert.Less(a, b);
            Assert.Less(b, c);
            Assert.AreEqual(0f, DashCueMath.HapticAmplitude(DashCueMath.CueLaunch, 0f));
            Assert.AreEqual(c * 0.5f, DashCueMath.HapticAmplitude(DashCueMath.CueLaunch, 0.5f), 1e-6f);
            Assert.AreEqual(0f, DashCueMath.HapticAmplitude(DashCueMath.CueStop, 1f));
        }
    }
}
