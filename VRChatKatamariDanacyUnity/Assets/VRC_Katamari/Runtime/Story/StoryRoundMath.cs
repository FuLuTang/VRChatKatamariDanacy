using VRCKatamari.Core;

namespace VRCKatamari.Story
{
    /// <summary>
    /// Which quest a story level sets: a size goal or a category count goal.
    /// </summary>
    /// <remarks>
    /// One value per story quest type that has a level:
    /// reach_target_diameter is <see cref="ReachTargetDiameter"/>,
    /// collect_category_count is <see cref="CollectCategoryCount"/>
    /// (Docs/Design/GameModes/README.md sections 2.1 and 4.3). The third quest type
    /// the design lists, absorb_specific_target, is deliberately absent: no level
    /// uses it, and the map has no unique target instance to name
    /// (README section 6-B6). A level sets exactly one quest type.
    /// </remarks>
    public enum StoryGoalType
    {
        ReachTargetDiameter = 0,
        CollectCategoryCount = 1
    }

    /// <summary>
    /// The star table and the round clock of one story round: the only arithmetic
    /// between a settled measurement and the level's thresholds.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the whole evaluation rule of a story round. A level is a target plus
    /// three star thresholds plus a time limit; the round measures one number at the
    /// instant the clock runs out and these calls turn that number into stars.
    /// Nothing here reads a clock, a scene or a motor: the caller passes the settled
    /// measurement and the numbers it took from the level data, so the rule can be
    /// tested without a Unity scene and the round has no second copy of it.
    /// </para>
    /// <para>
    /// Threshold source. The level JSON (Docs/Design/GameModes/Levels/
    /// story_01_bench_size.json, story_02_fruit_ten.json) carries three
    /// strictly increasing tiers, and validator rule 9 of the GameModes README pins
    /// tier 1 to the quest objective itself. Tier 1 is therefore not a parameter of
    /// these calls: it is the target, and a caller cannot accidentally evaluate with
    /// a first tier that disagrees with the quest the HUD shows.
    /// </para>
    /// <para>
    /// Comparisons are inclusive at every tier, which is what the design states: the
    /// size quest is "ball collision diameter at least target_diameter_m"
    /// (README section 2.1), and the 1.25 m size gate absorbs at exactly 1.25 m
    /// (README section 3.1, arithmetic item 6). A measurement exactly equal to a
    /// threshold has reached that tier.
    /// </para>
    /// <para>
    /// Stars are a lookup of the highest reached tier, never a weighted score, a
    /// progress fraction or an interpolation between tiers (README section 2.4: the
    /// star count is decided by the one settled number, not by the route). Zero stars
    /// means "quest not achieved" and is not softened into a fraction of a star.
    /// </para>
    /// <para>
    /// The partial band. GameModes README section 2.4: a level whose measurement misses
    /// the target but lands inside evaluation.partial_band times the target gets a
    /// different presenter line ("just barely"), and that line changes no star. The
    /// band is reported as its own out value and is only ever true together with zero
    /// stars, so a caller cannot show two verdicts for one result. The band edge is
    /// compared as the float product target * band, computed once, and is inclusive.
    /// </para>
    /// <para>
    /// Failure is a false return, never an exception, and every out value is left at
    /// its defined zero in that case so a caller that ignores the return cannot apply
    /// a half-computed verdict. Refused inputs are: any non-finite float, a negative
    /// measurement or count, a non-positive target, a tier below the tier before it
    /// (the level schema requires strictly increasing tiers; equal tiers are accepted
    /// because they are reachable and only make a lower tier redundant), and a band
    /// outside (0, 1]. The schema narrows the band further to [0.5, 1.0] (validator
    /// rule 10); that narrower range is the level author's contract, not a rule of
    /// the arithmetic.
    /// </para>
    /// <para>
    /// UdonSharp: static class, static methods only, no fields, no overloads, no
    /// allocation. Lengths are metres and times are seconds on the caller's clock,
    /// both in the project's single world space and its single time base.
    /// </para>
    /// </remarks>
    public static class StoryRoundMath
    {
        /// <summary>Stars for an unmet quest. The round failed; retry policy is not here.</summary>
        public const int StarsNone = 0;

        /// <summary>Highest star tier a level may define.</summary>
        public const int StarsMax = 3;

        /// <summary>
        /// Evaluates a reach_target_diameter round: stars for a settled diameter.
        /// </summary>
        /// <remarks>
        /// diameterM is the settled ball collision diameter in metres (the quantity
        /// the design evaluates, README section 2.1) and must be non-negative and
        /// finite. targetDiameterM is tier 1 (also the quest objective) and
        /// star2DiameterM / star3DiameterM are the next two tiers; all three must be
        /// positive, and each tier must be at least the tier before it.
        /// partialBand is evaluation.partial_band.
        /// <para>
        /// stars is the highest tier reached (0..3) and insidePartialBand is true only when
        /// stars is 0 and the diameter reached the band. A measurement on a tier
        /// boundary has reached that tier; a measurement below the band is neither
        /// achieved nor partial.
        /// </para>
        /// </remarks>
        public static bool TryEvaluateSizeStars(float diameterM, float targetDiameterM,
            float star2DiameterM, float star3DiameterM, float partialBand,
            out int stars, out bool insidePartialBand)
        {
            stars = StarsNone;
            insidePartialBand = false;

            if (!KatamariMath.IsFiniteFloat(diameterM)) return false;
            if (!KatamariMath.IsFiniteFloat(targetDiameterM)) return false;
            if (!KatamariMath.IsFiniteFloat(star2DiameterM)) return false;
            if (!KatamariMath.IsFiniteFloat(star3DiameterM)) return false;
            if (!KatamariMath.IsFiniteFloat(partialBand)) return false;

            if (diameterM < 0f) return false;
            if (targetDiameterM <= 0f) return false;
            if (star2DiameterM < targetDiameterM) return false;
            if (star3DiameterM < star2DiameterM) return false;
            if (partialBand <= 0f || partialBand > 1f) return false;

            if (diameterM >= star3DiameterM)
            {
                stars = 3;
                return true;
            }
            if (diameterM >= star2DiameterM)
            {
                stars = 2;
                return true;
            }
            if (diameterM >= targetDiameterM)
            {
                stars = 1;
                return true;
            }

            // Unmet. The band decides the presenter's line only, never the stars.
            float partialFromM = targetDiameterM * partialBand;
            insidePartialBand = diameterM >= partialFromM;
            return true;
        }

        /// <summary>
        /// Evaluates a collect_category_count round: stars for a settled attachment
        /// count inside the quest's category.
        /// </summary>
        /// <remarks>
        /// count is the settled count of confirmed attachments in the level's
        /// category registry key (README section 3.2, "counting rules": confirmed
        /// attachments only, one per item instance) and must be non-negative.
        /// targetCount is tier 1 (also the quest objective) and must be at least 1;
        /// star2Count / star3Count are the next tiers and must be non-decreasing.
        /// partialBand is evaluation.partial_band.
        /// <para>
        /// The band on a count is compared before rounding: the threshold is
        /// targetCount * partialBand as a float, so 10 items with band 0.9 makes 9
        /// items partial, which is the design's worked example (README section 3.2:
        /// "nine items, just one short"). Nine items is inside the band and eight is
        /// not, because that product is exactly nine and the comparison is inclusive.
        /// </para>
        /// </remarks>
        public static bool TryEvaluateCountStars(int count, int targetCount,
            int star2Count, int star3Count, float partialBand,
            out int stars, out bool insidePartialBand)
        {
            stars = StarsNone;
            insidePartialBand = false;

            if (!KatamariMath.IsFiniteFloat(partialBand)) return false;
            if (count < 0) return false;
            if (targetCount < 1) return false;
            if (star2Count < targetCount) return false;
            if (star3Count < star2Count) return false;
            if (partialBand <= 0f || partialBand > 1f) return false;

            if (count >= star3Count)
            {
                stars = 3;
                return true;
            }
            if (count >= star2Count)
            {
                stars = 2;
                return true;
            }
            if (count >= targetCount)
            {
                stars = 1;
                return true;
            }

            float partialFromCount = (float)targetCount * partialBand;
            insidePartialBand = (float)count >= partialFromCount;
            return true;
        }

        /// <summary>
        /// Remaining round time in seconds, clamped to [0, timeLimitSeconds].
        /// </summary>
        /// <remarks>
        /// startTimeSeconds is when the round began and nowSeconds is the caller's
        /// current clock reading, both on the same monotonic clock in seconds
        /// (UnityEngine.Time.time); timeLimitSeconds is the level's time_limit_s and
        /// must be positive and finite. The result never goes negative (an expired
        /// round reads 0, so a HUD cannot show "-3 s") and never exceeds the limit (a
        /// clock read before the recorded start reads the full limit, which is what a
        /// frame that ran during startup should show). Non-finite input, a
        /// non-positive limit and a difference that is not representable are refused.
        /// </remarks>
        public static bool TryRemainingSeconds(float startTimeSeconds, float nowSeconds,
            float timeLimitSeconds, out float remainingSeconds)
        {
            remainingSeconds = 0f;

            if (!KatamariMath.IsFiniteFloat(startTimeSeconds)) return false;
            if (!KatamariMath.IsFiniteFloat(nowSeconds)) return false;
            if (!KatamariMath.IsFiniteFloat(timeLimitSeconds)) return false;
            if (timeLimitSeconds <= 0f) return false;

            float elapsedSeconds = nowSeconds - startTimeSeconds;
            float remaining = timeLimitSeconds - elapsedSeconds;
            if (!KatamariMath.IsFiniteFloat(remaining)) return false;

            if (remaining > timeLimitSeconds) remaining = timeLimitSeconds;
            if (remaining < 0f) remaining = 0f;
            remainingSeconds = remaining;
            return true;
        }
    }
}
