using VRCKatamari.Core;

namespace VRCKatamari.Story
{
    /// <summary>
    /// The star table of one story round: the only arithmetic between a settled
    /// measurement and the level's thresholds.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the whole scoring rule of a story round, and it is the only place that
    /// knows scoring exists. A level is a target plus three star thresholds plus a time
    /// limit; the round measures one number at the instant the clock runs out and these
    /// calls turn that number into stars. The round itself does not call them: it reports
    /// the measurement as a fact and an optional presenter
    /// (<see cref="StoryResultPresenter"/>) applies this table. Remove the presenter from
    /// a scene and the round still settles - it just never shows stars.
    /// </para>
    /// <para>
    /// Nothing here reads a clock, a scene or a motor: the caller passes the settled
    /// measurement and the numbers it took from the level data, so the rule can be
    /// tested without a Unity scene and there is no second copy of it anywhere.
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
    /// allocation. Lengths are metres on the project's single world scale.
    /// </para>
    /// </remarks>
    public static class StoryStarMath
    {
        /// <summary>Stars for an unmet quest. The round failed; retry policy is not here.</summary>
        public const int StarsNone = 0;

        /// <summary>Highest star tier a level may define.</summary>
        public const int StarsMax = 3;

        /// <summary>
        /// Evaluates a type A round (<see cref="StoryGoalType.A"/>): stars for a settled diameter.
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
        /// Evaluates a type B round (<see cref="StoryGoalType.B"/>): stars for a settled count of
        /// confirmed attachments of the level's objective items.
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
    }
}
