namespace VRCKatamari.Story
{
    /// <summary>
    /// The per-level lookup rules of a multi-level round controller and its result
    /// presenter: which index is valid, what an empty or short table means, and when a
    /// level may be abandoned.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Why a static class. The controller and the presenter keep their per-level data in
    /// parallel arrays (UdonSharp cannot serialize a nested or struct table), and both
    /// must read those arrays by the same rule: an empty array, or an index past the end,
    /// means "no per-level data here, keep today's single-level field". Keeping the rule
    /// in one pure place is what makes "an old scene with no arrays behaves exactly as
    /// before" testable without a scene.
    /// </para>
    /// <para>
    /// UdonSharp: static class, static methods only, no fields, no overloads, no
    /// allocation.
    /// </para>
    /// </remarks>
    public static class StoryLevelMath
    {
        /// <summary>The level index before any level was selected (the scene's own single level).</summary>
        public const int NoLevel = -1;

        /// <summary>True when <paramref name="index"/> is a valid slot of a table of <paramref name="length"/> entries.</summary>
        public static bool IsIndexValid(int index, int length)
        {
            return index >= 0 && index < length;
        }

        /// <summary>
        /// Whether the controller accepts a level switch in this state: only before a round
        /// has started (Idle, 0) or after it ended (Result, 3). Running and Presenting are
        /// refused so a switch can never cut a live round or the presentation beat short.
        /// </summary>
        public static bool CanSelectInState(int state)
        {
            return state == 0 || state == 3;
        }

        /// <summary>
        /// Whether a Running round of this level may be abandoned. A null or short table,
        /// or no selected level, is false: that is today's "no leaving mid-round" rule.
        /// </summary>
        public static bool CanAbortLevel(bool[] allowAbort, int levelIndex)
        {
            if (allowAbort == null) return false;
            if (!IsIndexValid(levelIndex, allowAbort.Length)) return false;
            return allowAbort[levelIndex];
        }

        /// <summary>Per-level float, or <paramref name="fallback"/> when the table has no entry for the index.</summary>
        public static float PickFloat(float[] table, int index, float fallback)
        {
            if (table == null) return fallback;
            if (!IsIndexValid(index, table.Length)) return fallback;
            return table[index];
        }

        /// <summary>Per-level int, or <paramref name="fallback"/> when the table has no entry for the index.</summary>
        public static int PickInt(int[] table, int index, int fallback)
        {
            if (table == null) return fallback;
            if (!IsIndexValid(index, table.Length)) return fallback;
            return table[index];
        }

        /// <summary>Per-level bool, or <paramref name="fallback"/> when the table has no entry for the index.</summary>
        public static bool PickBool(bool[] table, int index, bool fallback)
        {
            if (table == null) return fallback;
            if (!IsIndexValid(index, table.Length)) return fallback;
            return table[index];
        }

        /// <summary>
        /// Whether the result card draws stars for this level. The table is "has star
        /// thresholds"; no table, or no entry, means yes (today's behaviour). A level
        /// flagged false (the crab level) shows its measurement only.
        /// </summary>
        public static bool LevelUsesStars(bool[] hasStars, int levelIndex, bool singleLevelHasStars)
        {
            return PickBool(hasStars, levelIndex, singleLevelHasStars);
        }
    }
}
