using NUnit.Framework;
using VRCKatamari.Pickup;

namespace VRCKatamari.Tests.EditMode.Pickup
{
    public class AbsorbFeedbackMathTests
    {
        [Test]
        public void GainIncreasesAndStopsAtFourItems()
        {
            Assert.AreEqual(0f, AbsorbFeedbackMath.AudioGain(0));
            Assert.Less(AbsorbFeedbackMath.AudioGain(1), AbsorbFeedbackMath.AudioGain(2));
            Assert.Less(AbsorbFeedbackMath.AudioGain(2), AbsorbFeedbackMath.AudioGain(3));
            Assert.Less(AbsorbFeedbackMath.AudioGain(3), AbsorbFeedbackMath.AudioGain(4));
            Assert.AreEqual(0.24f, AbsorbFeedbackMath.AudioGain(30));
        }

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

        [Test]
        public void AudioAndHapticDeadlinesAreIndependent()
        {
            float start = 1f;
            float audio = start + AbsorbFeedbackMath.AudioWindowSeconds;
            float haptic = start + AbsorbFeedbackMath.HapticIntervalSeconds;
            Assert.IsFalse(AbsorbFeedbackMath.IsDue(start + 0.059f, audio));
            Assert.IsTrue(AbsorbFeedbackMath.IsDue(audio, audio));
            Assert.IsFalse(AbsorbFeedbackMath.IsDue(audio, haptic));
            Assert.IsTrue(AbsorbFeedbackMath.IsDue(haptic, haptic));
        }

        [Test]
        public void ClusterPrefersSpecialThenSizeThenFirstArrival()
        {
            Assert.IsTrue(AbsorbFeedbackMath.PreferAudioClass(0, 0, 0, 0, 0));
            Assert.IsTrue(AbsorbFeedbackMath.PreferAudioClass(1, 0, 2, 1, 0));
            Assert.IsTrue(AbsorbFeedbackMath.PreferAudioClass(2, 1, 0, 2, 1));
            Assert.IsFalse(AbsorbFeedbackMath.PreferAudioClass(3, 2, 1, 3, 1));
            Assert.IsFalse(AbsorbFeedbackMath.PreferAudioClass(4, 2, 1, 0, 2));
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
        public void ConsecutiveClustersNeverReplayTheSameClip()
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
