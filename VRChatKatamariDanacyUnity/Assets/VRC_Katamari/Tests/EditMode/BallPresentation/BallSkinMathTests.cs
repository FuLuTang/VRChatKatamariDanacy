using NUnit.Framework;
using VRCKatamari.BallPresentation;

namespace VRCKatamari.Tests
{
    public class BallSkinMathTests
    {
        private static readonly int[] Some = { 1, 5, 28 };

        [TestCase(1, true)]
        [TestCase(25, true)]
        [TestCase(26, false)]
        [TestCase(27, false)]
        [TestCase(28, true)]
        [TestCase(29, true)]
        [TestCase(0, false)]
        [TestCase(30, false)]
        public void OriginalNumbersSkip26And27(int number, bool expected)
        {
            Assert.AreEqual(expected, BallSkinMath.IsOriginalNumber(number));
        }

        [Test]
        public void TheOriginalHas27Skins()
        {
            int count = 0;
            for (int n = BallSkinMath.FirstNumber; n <= BallSkinMath.LastNumber; n++)
                if (BallSkinMath.IsOriginalNumber(n)) count++;
            Assert.AreEqual(27, count);
        }

        [Test]
        public void SteppingWrapsOverTheAvailableSkins()
        {
            Assert.AreEqual(5, BallSkinMath.Stepped(Some, 1, 1));
            Assert.AreEqual(28, BallSkinMath.Stepped(Some, 5, 1));
            Assert.AreEqual(1, BallSkinMath.Stepped(Some, 28, 1));
            Assert.AreEqual(28, BallSkinMath.Stepped(Some, 1, -1));
            Assert.AreEqual(5, BallSkinMath.Stepped(Some, 5, 0));
        }

        [Test]
        public void SteppingFromAnUnavailableSkinLandsOnTheDefault()
        {
            Assert.AreEqual(1, BallSkinMath.Stepped(Some, 7, 1));
            Assert.AreEqual(5, BallSkinMath.Stepped(new[] { 5, 9 }, 7, -1));
        }

        [Test]
        public void SingleSkinStaysPut()
        {
            Assert.AreEqual(1, BallSkinMath.Stepped(new[] { 1 }, 1, 1));
            Assert.AreEqual(1, BallSkinMath.Stepped(new[] { 1 }, 1, -1));
        }

        [Test]
        public void WithoutAListEveryOriginalNumberIsReachable()
        {
            Assert.AreEqual(28, BallSkinMath.Stepped(null, 25, 1));
            Assert.AreEqual(25, BallSkinMath.Stepped(new int[0], 28, -1));
            Assert.AreEqual(1, BallSkinMath.Stepped(null, 29, 1));
            Assert.AreEqual(29, BallSkinMath.Stepped(null, 1, -1));
            int number = 1;
            for (int i = 0; i < 27; i++) number = BallSkinMath.Stepped(null, number, 1);
            Assert.AreEqual(1, number, "27 presses go round once");
        }

        [Test]
        public void StoredChoiceNormalizesToWhatTheBallHas()
        {
            Assert.AreEqual(5, BallSkinMath.Normalize(Some, 5));
            Assert.AreEqual(1, BallSkinMath.Normalize(Some, 7), "unavailable -> default");
            Assert.AreEqual(5, BallSkinMath.Normalize(new[] { 5, 9 }, 7), "no default -> first");
            Assert.AreEqual(12, BallSkinMath.Normalize(null, 12));
            Assert.AreEqual(1, BallSkinMath.Normalize(null, 27));
            Assert.AreEqual(1, BallSkinMath.Normalize(null, -3));
        }

        [Test]
        public void ValidListsAreAscendingOriginalNumbers()
        {
            Assert.IsTrue(BallSkinMath.IsValidList(Some));
            Assert.IsFalse(BallSkinMath.IsValidList(null));
            Assert.IsFalse(BallSkinMath.IsValidList(new int[0]));
            Assert.IsFalse(BallSkinMath.IsValidList(new[] { 5, 1 }));
            Assert.IsFalse(BallSkinMath.IsValidList(new[] { 1, 1 }));
            Assert.IsFalse(BallSkinMath.IsValidList(new[] { 1, 26 }));
            Assert.AreEqual(2, BallSkinMath.IndexOf(Some, 28));
            Assert.AreEqual(-1, BallSkinMath.IndexOf(Some, 2));
            Assert.AreEqual(-1, BallSkinMath.IndexOf(null, 1));
        }

        [TestCase(1, "No.01")]
        [TestCase(29, "No.29")]
        public void LabelIsTwoDigits(int number, string text)
        {
            Assert.AreEqual(text, BallSkinMath.Label(number));
        }
    }
}
