using NUnit.Framework;
using UnityEngine;
using VRCKatamari.Story;

namespace VRCKatamari.Tests
{
    /// <summary>
    /// EditMode tests for the story round's star table and clock: the numbers both
    /// story levels (Docs/Design/GameModes/Levels/story_01_bench_size.json and
    /// story_02_fruit_ten.json) ask for, plus the edges a level author can get wrong.
    /// </summary>
    /// <remarks>
    /// Every expectation is read off the design, not off the implementation:
    /// a tier is reached at exactly its threshold because the quest is "at least the
    /// target" (GameModes README section 2.1) and the 1.25 m size gate absorbs at
    /// exactly 1.25 m (section 3.1, arithmetic item 6); zero stars is failure and the
    /// partial band only changes the presenter's line, never the stars (section 2.4);
    /// the band's worked example is "nine fruit out of ten" (section 3.2). Boundary
    /// cases use the level's own numbers (1.25 / 1.45 / 1.60 m, 10 / 20 / 30 items,
    /// band 0.90) so a changed level and a changed rule cannot both stay green.
    /// <para>
    /// Only the static math class is touched: an UdonSharpBehaviour cannot be
    /// constructed in EditMode, which is why the round's star rule lives in
    /// StoryRoundMath and not in the controller.
    /// </para>
    /// </remarks>
    public class StoryRoundMathTests
    {
        private const float Band = 0.9f;

        // Level 1's star table: 1.25 / 1.45 / 1.60 m.
        private const float TargetM = 1.25f;
        private const float Star2M = 1.45f;
        private const float Star3M = 1.6f;

        [Test]
        public void SizeGoalAtTheTargetHasReachedTierOne()
        {
            int stars;
            bool insidePartialBand;

            // Exactly on the threshold: the quest is "at least 1.25 m", so this counts.
            Assert.IsTrue(StoryRoundMath.TryEvaluateSizeStars(TargetM, TargetM, Star2M,
                Star3M, Band, out stars, out insidePartialBand));
            Assert.AreEqual(1, stars);
            Assert.IsFalse(insidePartialBand);

            // A hair below is not achieved, and 1.2499 m is inside the 0.90 band, so it
            // is the "just barely" case rather than a plain failure.
            Assert.IsTrue(StoryRoundMath.TryEvaluateSizeStars(1.2499f, TargetM, Star2M,
                Star3M, Band, out stars, out insidePartialBand));
            Assert.AreEqual(0, stars);
            Assert.IsTrue(insidePartialBand);
        }

        [Test]
        public void SizeGoalAwardsTheHighestTierReached()
        {
            int stars;
            bool insidePartialBand;

            // 1.45 m is tier 2 exactly; 1.5999 m is still tier 2; 1.60 m is tier 3.
            Assert.IsTrue(StoryRoundMath.TryEvaluateSizeStars(Star2M, TargetM, Star2M,
                Star3M, Band, out stars, out insidePartialBand));
            Assert.AreEqual(2, stars);

            Assert.IsTrue(StoryRoundMath.TryEvaluateSizeStars(1.5999f, TargetM, Star2M,
                Star3M, Band, out stars, out insidePartialBand));
            Assert.AreEqual(2, stars);

            Assert.IsTrue(StoryRoundMath.TryEvaluateSizeStars(Star3M, TargetM, Star2M,
                Star3M, Band, out stars, out insidePartialBand));
            Assert.AreEqual(3, stars);

            // Above tier 3 stays at 3: there is no fourth tier to reach.
            Assert.IsTrue(StoryRoundMath.TryEvaluateSizeStars(2.2f, TargetM, Star2M,
                Star3M, Band, out stars, out insidePartialBand));
            Assert.AreEqual(StoryRoundMath.StarsMax, stars);
            Assert.IsFalse(insidePartialBand);
        }

        [Test]
        public void SizeGoalBelowThePartialBandIsNeitherAchievedNorPartial()
        {
            int stars;
            bool insidePartialBand;

            // The band starts at 1.25 * 0.90 = 1.125 m; 1.00 m is well below it. This is
            // the design's plain failure line, not the "just barely" one.
            Assert.IsTrue(StoryRoundMath.TryEvaluateSizeStars(1f, TargetM, Star2M, Star3M,
                Band, out stars, out insidePartialBand));
            Assert.AreEqual(StoryRoundMath.StarsNone, stars);
            Assert.IsFalse(insidePartialBand);

            // The start size of a story ball (0.29974 m) is far below the band too: an
            // untouched round must be a plain failure, never a partial credit.
            Assert.IsTrue(StoryRoundMath.TryEvaluateSizeStars(0.29974f, TargetM, Star2M,
                Star3M, Band, out stars, out insidePartialBand));
            Assert.AreEqual(0, stars);
            Assert.IsFalse(insidePartialBand);
        }

        [Test]
        public void SizePartialBandEdgeIsInclusiveAtTheProductsValue()
        {
            int stars;
            bool insidePartialBand;

            // The edge is the float product the rule computes, so passing that product
            // back in pins the comparison as inclusive rather than as a strict one.
            float bandEdgeM = TargetM * Band;
            Assert.IsTrue(StoryRoundMath.TryEvaluateSizeStars(bandEdgeM, TargetM, Star2M,
                Star3M, Band, out stars, out insidePartialBand));
            Assert.AreEqual(0, stars);
            Assert.IsTrue(insidePartialBand);

            // Just below the edge is a plain failure.
            Assert.IsTrue(StoryRoundMath.TryEvaluateSizeStars(bandEdgeM - 0.0001f, TargetM,
                Star2M, Star3M, Band, out stars, out insidePartialBand));
            Assert.AreEqual(0, stars);
            Assert.IsFalse(insidePartialBand);
        }

        [Test]
        public void CountGoalAwardsTiersAtItsBoundaries()
        {
            // Level 2's star table: 10 / 20 / 30 fruit.
            AssertCountStars(10, 1, false);
            AssertCountStars(19, 1, false);
            AssertCountStars(20, 2, false);
            AssertCountStars(29, 2, false);
            AssertCountStars(30, 3, false);
            AssertCountStars(50, 3, false);
        }

        [Test]
        public void CountGoalPartialBandCoversNineOfTen()
        {
            // The design's worked example (README section 3.2): nine items is "just one
            // short" of ten with band 0.90, and it is still zero stars. Eight items is
            // outside the band.
            AssertCountStars(9, 0, true);
            AssertCountStars(8, 0, false);
            AssertCountStars(0, 0, false);
        }

        [Test]
        public void CountGoalBandEdgeIsInclusiveAtTheProductsValue()
        {
            int stars;
            bool insidePartialBand;

            // Ten items with band 0.90 puts the edge at nine, which is exactly the
            // design's "just one short" example. Rounding up states the edge in the
            // test instead of hiding it in a truncating cast.
            int bandEdgeCount = Mathf.CeilToInt(10f * Band);
            Assert.AreEqual(9, bandEdgeCount);
            Assert.IsTrue(StoryRoundMath.TryEvaluateCountStars(bandEdgeCount, 10, 20,
                30, Band, out stars, out insidePartialBand));
            Assert.AreEqual(0, stars);
            Assert.IsTrue(insidePartialBand);
        }

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
        public void SizeEvaluationRefusesInvalidInputs()
        {
            int stars;
            bool insidePartialBand;

            // One refused variant per gate, with everything else valid.
            AssertSizeRefused(float.NaN, TargetM, Star2M, Star3M, Band);
            AssertSizeRefused(float.PositiveInfinity, TargetM, Star2M, Star3M, Band);
            AssertSizeRefused(-0.01f, TargetM, Star2M, Star3M, Band);
            AssertSizeRefused(TargetM, float.NaN, Star2M, Star3M, Band);
            AssertSizeRefused(TargetM, 0f, Star2M, Star3M, Band);
            AssertSizeRefused(TargetM, -1f, Star2M, Star3M, Band);
            // A tier below the tier before it cannot be represented: it would make a
            // lower tier unreachable, so the level data is wrong and the call says so.
            AssertSizeRefused(TargetM, TargetM, 1.2f, Star3M, Band);
            AssertSizeRefused(TargetM, TargetM, Star2M, 1.4f, Band);
            // The band is a fraction of the target.
            AssertSizeRefused(TargetM, TargetM, Star2M, Star3M, 0f);
            AssertSizeRefused(TargetM, TargetM, Star2M, Star3M, -0.5f);
            AssertSizeRefused(TargetM, TargetM, Star2M, Star3M, 1.5f);
            AssertSizeRefused(TargetM, TargetM, Star2M, Star3M, float.NaN);

            // A refused call leaves the out values at their defined zero, so a caller
            // that ignored the false cannot award a star or claim a partial credit.
            Assert.IsFalse(StoryRoundMath.TryEvaluateSizeStars(float.NaN, TargetM, Star2M,
                Star3M, Band, out stars, out insidePartialBand));
            Assert.AreEqual(0, stars);
            Assert.IsFalse(insidePartialBand);
        }

        [Test]
        public void CountEvaluationRefusesInvalidInputs()
        {
            int stars;
            bool insidePartialBand;

            AssertCountRefused(-1, 10, 20, 30, Band);
            AssertCountRefused(10, 0, 20, 30, Band);
            AssertCountRefused(10, -1, 20, 30, Band);
            AssertCountRefused(10, 10, 9, 30, Band);
            AssertCountRefused(10, 10, 20, 19, Band);
            AssertCountRefused(10, 10, 20, 30, 0f);
            AssertCountRefused(10, 10, 20, 30, 1.5f);

            Assert.IsFalse(StoryRoundMath.TryEvaluateCountStars(-1, 10, 20, 30, Band,
                out stars, out insidePartialBand));
            Assert.AreEqual(0, stars);
            Assert.IsFalse(insidePartialBand);
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

        [Test]
        public void EqualTiersAreAcceptedAndTheHighestOneWins()
        {
            int stars;
            bool insidePartialBand;

            // The level schema asks for strictly increasing tiers (validator rule 9),
            // but a level that repeats a tier is still evaluable: the highest reached
            // tier wins, so a redundant tier 2 cannot block tier 3.
            Assert.IsTrue(StoryRoundMath.TryEvaluateSizeStars(Star3M, Star3M, Star3M, Star3M,
                1f, out stars, out insidePartialBand));
            Assert.AreEqual(3, stars);

            Assert.IsTrue(StoryRoundMath.TryEvaluateSizeStars(0.5f, 0.5f, 0.5f, 0.5f, 1f,
                out stars, out insidePartialBand));
            Assert.AreEqual(3, stars);
        }

        private static void AssertCountStars(int count, int expectedStars,
            bool expectedInsidePartialBand)
        {
            int stars;
            bool insidePartialBand;
            Assert.IsTrue(StoryRoundMath.TryEvaluateCountStars(count, 10, 20, 30, Band,
                out stars, out insidePartialBand), "count " + count + " should evaluate");
            Assert.AreEqual(expectedStars, stars, "count " + count + " stars");
            Assert.AreEqual(expectedInsidePartialBand, insidePartialBand,
                "count " + count + " partial band");
        }

        private static void AssertCountRefused(int count, int targetCount, int star2Count,
            int star3Count, float band)
        {
            int stars;
            bool insidePartialBand;
            Assert.IsFalse(StoryRoundMath.TryEvaluateCountStars(count, targetCount,
                star2Count, star3Count, band, out stars, out insidePartialBand),
                "count " + count + " with tiers " + targetCount + "/" + star2Count + "/"
                + star3Count + " and band " + band + " should be refused");
            Assert.AreEqual(0, stars);
            Assert.IsFalse(insidePartialBand);
        }

        private static void AssertSizeRefused(float diameterM, float targetM, float star2M,
            float star3M, float band)
        {
            int stars;
            bool insidePartialBand;
            Assert.IsFalse(StoryRoundMath.TryEvaluateSizeStars(diameterM, targetM, star2M,
                star3M, band, out stars, out insidePartialBand),
                "diameter " + diameterM + " with tiers " + targetM + "/" + star2M + "/"
                + star3M + " and band " + band + " should be refused");
            Assert.AreEqual(0, stars);
            Assert.IsFalse(insidePartialBand);
        }
    }
}
