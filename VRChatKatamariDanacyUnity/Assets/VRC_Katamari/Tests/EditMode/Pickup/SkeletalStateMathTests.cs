using NUnit.Framework;
using VRCKatamari.Pickup;

namespace VRCKatamari.Tests
{
    /// <summary>
    /// Pins <see cref="SkeletalStateMath"/>: the original animation number (<c>playAnimNo</c>,
    /// REROLL <c>mononame.ANIM_NAME</c> index) to our state name. Pure; runs on plain .NET.
    /// </summary>
    public class SkeletalStateMathTests
    {
        [Test]
        public void AnimNoFollowsTheOriginalAnimNameOrder()
        {
            string[] expected = { "idle", "walk", "rampage", "escape", "chase", "special" };
            Assert.AreEqual(expected.Length, SkeletalStateMath.AnimNoCount);
            for (int i = 0; i < expected.Length; i++)
                Assert.AreEqual(expected[i], SkeletalStateMath.StateOfAnimNo(i), "playAnimNo " + i);
        }

        [Test]
        public void AnimNoIsNotThePs2SlotOrder()
        {
            // PS2 slot 1 is escape and slot 3 idle; animation number 1 is walk and 3 escape.
            Assert.AreEqual(SkeletalStateMath.Walk, SkeletalStateMath.StateOfAnimNo(1));
            Assert.AreEqual(SkeletalStateMath.Escape, SkeletalStateMath.StateOfAnimNo(3));
            Assert.AreEqual(SkeletalStateMath.Idle, SkeletalStateMath.StateOfAnimNo(0));
        }

        [Test]
        public void NumbersOutsideTheTableHaveNoState()
        {
            Assert.AreEqual("", SkeletalStateMath.StateOfAnimNo(-1));
            Assert.AreEqual("", SkeletalStateMath.StateOfAnimNo(SkeletalStateMath.AnimNoCount));
        }
    }
}
