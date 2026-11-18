using NUnit.Framework;
using VRCKatamari.Hub;

namespace VRCKatamari.Tests
{
    /// <summary>The island on/off rule: one active island, none while parked or before the first switch.</summary>
    public class IslandActivationMathTests
    {
        [Test]
        public void NoIslandIsOnBeforeTheFirstSwitch()
        {
            for (int i = 0; i < 4; i++)
            {
                Assert.IsFalse(IslandActivationMath.ShouldBeActive(i, -1, false));
            }
            Assert.AreEqual(0, IslandActivationMath.ActiveCount(4, -1, false));
        }

        [Test]
        public void OnlyTheActiveLevelsIslandIsOn()
        {
            for (int i = 0; i < 4; i++)
            {
                Assert.AreEqual(i == 2, IslandActivationMath.ShouldBeActive(i, 2, false));
            }
            Assert.AreEqual(1, IslandActivationMath.ActiveCount(4, 2, false));
        }

        [Test]
        public void ParkedMeansEveryIslandIsOff()
        {
            Assert.AreEqual(0, IslandActivationMath.ActiveCount(4, 2, true));
            Assert.IsFalse(IslandActivationMath.ShouldBeActive(2, 2, true));
        }

        [Test]
        public void OutOfRangeIndexIsNeverOn()
        {
            Assert.IsFalse(IslandActivationMath.ShouldBeActive(-1, 0, false));
            Assert.IsFalse(IslandActivationMath.ShouldBeActive(7, 0, false));
        }

        [Test]
        public void RootListMustMatchTheLevelList()
        {
            Assert.IsTrue(IslandActivationMath.RootsMatchLevels(4, 4));
            Assert.IsFalse(IslandActivationMath.RootsMatchLevels(3, 4));
            Assert.IsFalse(IslandActivationMath.RootsMatchLevels(5, 4));
            Assert.IsTrue(IslandActivationMath.RootsMatchLevels(0, 0));
        }
    }
}
