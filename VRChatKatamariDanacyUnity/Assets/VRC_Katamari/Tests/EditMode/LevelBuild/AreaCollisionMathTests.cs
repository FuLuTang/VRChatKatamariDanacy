using NUnit.Framework;
using VRCKatamari.Pickup;

namespace VRCKatamari.Tests.EditMode.LevelBuild
{
    public class AreaCollisionMathTests
    {
        [TestCase(-1, 2, true)]
        [TestCase(2, 2, true)]
        [TestCase(3, 2, false)]
        [TestCase(2, 0, false)]
        [TestCase(0, 0, true)]
        [TestCase(-1, -1, true)]
        [TestCase(2, -1, false)]
        public void CollisionNeedsWildcardOrMatchingArea(int hitOnAreaNo, int areaNo, bool expected)
        {
            Assert.That(AreaCollisionMath.CollidesInArea(hitOnAreaNo, areaNo), Is.EqualTo(expected));
        }
    }
}
