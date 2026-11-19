using NUnit.Framework;
using VRCKatamari.Diagnostics;

namespace VRCKatamari.Tests
{
    public class BallPerformanceMathTests
    {
        [Test]
        public void ConvertsTimestampDifferenceBeforeNarrowingToFloat()
        {
            const long start = 10000000000000000L;
            Assert.That(BallPerformanceMath.Milliseconds(start, start + 5L, 0.0001),
                Is.EqualTo(0.0005f).Within(0.0000001f));
            Assert.That(BallPerformanceMath.Milliseconds(start, start, 0.0001), Is.Zero);
            Assert.That(BallPerformanceMath.Milliseconds(start, start - 10L, 0.0001), Is.Zero);
        }

        [Test]
        public void KeepsLatestMeanAndPeakPerSlotIncludingZeroSamples()
        {
            var sum = new double[BallPerformanceMath.SlotCount];
            var max = new float[sum.Length];
            var last = new float[sum.Length];
            var count = new int[sum.Length];
            int steps = BallPerformanceMath.StepsPerFrame;
            int motion = BallPerformanceMath.Motion;
            BallPerformanceMath.Add(steps, 2f, sum, max, last, count);
            BallPerformanceMath.Add(steps, 0f, sum, max, last, count);
            BallPerformanceMath.Add(motion, 1.25f, sum, max, last, count);
            Assert.That(count[steps], Is.EqualTo(2));
            Assert.That(last[steps], Is.Zero);
            Assert.That(max[steps], Is.EqualTo(2f));
            Assert.That(BallPerformanceMath.Average(sum[steps], count[steps]), Is.EqualTo(1f));
            Assert.That(BallPerformanceMath.Average(sum[motion], count[motion]), Is.EqualTo(1.25f));
            Assert.That(BallPerformanceMath.Average(sum[motion], count[steps]), Is.EqualTo(0.625f));
        }

        [Test]
        public void NextWindowDoesNotInheritOldPeakOrSum()
        {
            int slot = BallPerformanceMath.TerrainSupport;
            var sum = new double[BallPerformanceMath.SlotCount];
            var max = new float[sum.Length];
            var last = new float[sum.Length];
            var count = new int[sum.Length];
            BallPerformanceMath.Add(slot, 99f, sum, max, last, count);
            BallPerformanceMath.ClearWindow(count, sum);
            Assert.That(count[slot], Is.Zero);
            Assert.That(BallPerformanceMath.Average(sum[slot], count[slot]), Is.Zero);
            BallPerformanceMath.Add(slot, 0.5f, sum, max, last, count);
            Assert.That(count[slot], Is.EqualTo(1));
            Assert.That(max[slot], Is.EqualTo(0.5f));
            Assert.That(last[slot], Is.EqualTo(0.5f));
            Assert.That(sum[slot], Is.EqualTo(0.5));
        }

        [TestCase(0f, 5f)]
        [TestCase(float.NaN, 5f)]
        [TestCase(1f, 1f)]
        [TestCase(10f, 10f)]
        [TestCase(60f, 60f)]
        [TestCase(61f, 5f)]
        public void IntervalHasBoundedLogCadence(float input, float seconds)
        {
            Assert.That(BallPerformanceMath.Interval(input), Is.EqualTo(seconds));
        }
    }
}
