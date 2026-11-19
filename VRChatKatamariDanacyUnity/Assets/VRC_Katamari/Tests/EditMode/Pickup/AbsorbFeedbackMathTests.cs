using NUnit.Framework;
using VRCKatamari.Pickup;

namespace VRCKatamari.Tests.EditMode.Pickup
{
    public class AbsorbFeedbackMathTests
    {
        [TestCase(0.14f, 0, 0.020f, 0.20f)]
        [TestCase(0.15f, 1, 0.028f, 0.27f)]
        [TestCase(0.30f, 2, 0.035f, 0.35f)]
        public void HapticsUseSizeAndIndependentSetting(float size, int band, float duration, float amplitude)
        {
            Assert.AreEqual(band, AbsorbFeedbackMath.SizeBand(size));
            Assert.AreEqual(duration, AbsorbFeedbackMath.HapticDuration(band));
            Assert.AreEqual(amplitude, AbsorbFeedbackMath.HapticAmplitude(band, 1f));
            Assert.AreEqual(amplitude * 0.5f, AbsorbFeedbackMath.HapticAmplitude(band, 0.5f));
            Assert.AreEqual(0f, AbsorbFeedbackMath.HapticAmplitude(band, 0f));
        }

        [TestCase(0, 4, true, 0, AbsorbFeedbackMath.RollInLayer | AbsorbFeedbackMath.ScreamLayer)]
        [TestCase(0, -1, true, 0, AbsorbFeedbackMath.RollInLayer)]
        [TestCase(0, 4, false, 0, AbsorbFeedbackMath.RollInLayer)]
        [TestCase(0, 4, true, -1, AbsorbFeedbackMath.RollInLayer)]
        [TestCase(0, 4, true, 2, AbsorbFeedbackMath.RollInLayer)]
        [TestCase(-1, 4, true, 1, AbsorbFeedbackMath.ScreamLayer)]
        [TestCase(-1, -1, false, -1, 0)]
        public void ScreamAddsToRollInWithoutReplacingIt(int rollIn, int scream, bool ready, int slot, int expected)
        {
            Assert.AreEqual(expected, AbsorbFeedbackMath.PlaybackLayers(rollIn, scream, ready, slot));
        }

        [Test]
        public void ScreamCooldownIsSharedByVariantsButIndependentOfOtherIds()
        {
            int[] ids = { 0, 0, 0, 12, 12, 13, 18, 18 };
            int first = AbsorbFeedbackMath.FirstScreamClip(ids, 12);
            int different = AbsorbFeedbackMath.FirstScreamClip(ids, 13);
            Assert.AreEqual(3, first);
            Assert.AreEqual(5, different);
            Assert.AreEqual(-1, AbsorbFeedbackMath.FirstScreamClip(ids, 0));
            Assert.AreEqual(-1, AbsorbFeedbackMath.FirstScreamClip(ids, 14));
            Assert.AreEqual(-1, AbsorbFeedbackMath.FirstScreamClip(ids, 20));

            float start = 5f;
            float[] deadlines = new float[ids.Length];
            deadlines[first] = AbsorbFeedbackMath.NextScreamTime(start);
            Assert.IsFalse(AbsorbFeedbackMath.IsDue(start + 89f / 30f, deadlines[first]));
            Assert.IsTrue(AbsorbFeedbackMath.IsDue(start + 90f / 30f, deadlines[first]));
            Assert.IsTrue(AbsorbFeedbackMath.IsDue(start, deadlines[different]));
        }

        [TestCase(false, false, 0)]
        [TestCase(false, true, 0)]
        [TestCase(true, false, 1)]
        [TestCase(true, true, -1)]
        public void AtMostTwoScreamsCanPlay(bool firstBusy, bool secondBusy, int expected)
        {
            Assert.AreEqual(2, AbsorbFeedbackMath.ScreamVoiceCount);
            Assert.AreEqual(expected, AbsorbFeedbackMath.FreeScreamSlot(firstBusy, secondBusy));
        }

        [Test]
        public void ClipRankSkipsPreviousUnlessOnlyChoice()
        {
            Assert.AreEqual(-1, AbsorbFeedbackMath.ClipRank(0, 0, -1));
            Assert.AreEqual(0, AbsorbFeedbackMath.ClipRank(123, 1, 0));
            Assert.AreEqual(2, AbsorbFeedbackMath.ClipRank(65535, 3, -1));
            for (int skip = 0; skip < 3; skip++)
            {
                int low = AbsorbFeedbackMath.ClipRank(0, 3, skip);
                int high = AbsorbFeedbackMath.ClipRank(65535, 3, skip);
                Assert.That(low, Is.InRange(0, 2).And.Not.EqualTo(skip));
                Assert.That(high, Is.InRange(0, 2).And.Not.EqualTo(skip));
                Assert.Less(low, high);
            }
        }

        [Test]
        public void RandomStateStaysInTheSixteenBitRangeAndIsDeterministic()
        {
            int state = 197;
            for (int step = 0; step < 1000; step++)
            {
                int next = AbsorbFeedbackMath.NextRandomState(state);
                Assert.That(next, Is.InRange(0, 65535));
                Assert.AreEqual(next, AbsorbFeedbackMath.NextRandomState(state));
                state = next;
            }
        }

        [Test]
        public void ConsecutiveRollInsNeverReplayTheSameVariant()
        {
            for (int count = 2; count <= 4; count++)
            {
                int state = 197;
                int previous = -1;
                for (int step = 0; step < 500; step++)
                {
                    state = AbsorbFeedbackMath.NextRandomState(state);
                    int rank = AbsorbFeedbackMath.ClipRank(state, count, previous);
                    Assert.That(rank, Is.InRange(0, count - 1));
                    if (previous >= 0) Assert.AreNotEqual(previous, rank);
                    previous = rank;
                }
            }
        }
    }
}
