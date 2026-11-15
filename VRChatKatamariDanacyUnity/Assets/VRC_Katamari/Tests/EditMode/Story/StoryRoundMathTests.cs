using NUnit.Framework;
using VRCKatamari.Story;

namespace VRCKatamari.Tests
{
    /// <summary>
    /// EditMode tests for the story round's clock: the countdown both levels run
    /// (120 s) and the clamps a HUD needs.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The clock is the round's own rule, and it has to hold whether or not the optional
    /// result presenter is in the scene: a round that could not time itself without a
    /// grader would put scoring back in the core loop. Nothing here reads a scene or a
    /// motor; the calls take their own time readings.
    /// </para>
    /// <para>
    /// Only the static math class is touched. A level's time_limit_s is constrained to
    /// 30..300 s by validator rule 10 of the GameModes README; the edges below are the
    /// refused inputs that constraint exists to prevent.
    /// </para>
    /// <para>
    /// The star table lives in StoryStarMath and is tested in StoryStarMathTests; the
    /// round-without-a-presenter claim is in StoryRoundControllerTests.
    /// </para>
    /// </remarks>
    public class StoryRoundMathTests
    {
        [Test]
        public void RemainingTimeCountsDownAndClampsAtZero()
        {
            float remaining;

            // 120 s limit started at t=100: at t=100 a full limit is left, at t=160
            // exactly half, at t=220 the round is over and reads zero, not -20.
            Assert.IsTrue(StoryRoundMath.TryRemainingSeconds(100f, 100f, 120f, out remaining));
            Assert.AreEqual(120f, remaining, 1e-4f);

            Assert.IsTrue(StoryRoundMath.TryRemainingSeconds(100f, 160f, 120f, out remaining));
            Assert.AreEqual(60f, remaining, 1e-4f);

            Assert.IsTrue(StoryRoundMath.TryRemainingSeconds(100f, 220f, 120f, out remaining));
            Assert.AreEqual(0f, remaining);

            Assert.IsTrue(StoryRoundMath.TryRemainingSeconds(100f, 1000f, 120f, out remaining));
            Assert.AreEqual(0f, remaining);
        }

        [Test]
        public void RemainingTimeNeverReportsMoreThanTheLimit()
        {
            float remaining;

            // A clock read before the round's recorded start (a frame that ran during
            // startup) shows the full limit rather than a value above it.
            Assert.IsTrue(StoryRoundMath.TryRemainingSeconds(100f, 90f, 120f, out remaining));
            Assert.AreEqual(120f, remaining, 1e-4f);
        }

        [Test]
        public void RemainingTimeRefusesInvalidInputs()
        {
            float remaining;

            Assert.IsFalse(StoryRoundMath.TryRemainingSeconds(float.NaN, 0f, 120f, out remaining));
            Assert.IsFalse(StoryRoundMath.TryRemainingSeconds(0f, float.PositiveInfinity, 120f,
                out remaining));
            Assert.IsFalse(StoryRoundMath.TryRemainingSeconds(0f, 0f, float.NaN, out remaining));
            // A level's time_limit_s is constrained to 30..300 s (validator rule 10); a
            // zero or negative limit cannot produce a countdown at all.
            Assert.IsFalse(StoryRoundMath.TryRemainingSeconds(0f, 0f, 0f, out remaining));
            Assert.IsFalse(StoryRoundMath.TryRemainingSeconds(0f, 0f, -5f, out remaining));
            Assert.AreEqual(0f, remaining);
        }
    }
}
