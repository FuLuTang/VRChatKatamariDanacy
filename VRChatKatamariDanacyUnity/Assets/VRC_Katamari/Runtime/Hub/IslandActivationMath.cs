namespace VRCKatamari.Hub
{
    /// <summary>
    /// Which level islands are switched on. One rule, no scene: at most one island is
    /// active, the one of the active level, and it is switched off while the player is
    /// parked in the hub room.
    /// </summary>
    /// <remarks>
    /// An island that is not active costs nothing: its renderers are not drawn and none of
    /// its behaviours (pickup field, motion, prop fall, terrain, arena) run Update. The
    /// state is a pure function of three facts, so <see cref="LevelSwitchHost"/> never
    /// keeps a per-island flag that could drift from them.
    /// </remarks>
    public static class IslandActivationMath
    {
        /// <summary>
        /// Whether island <paramref name="levelIndex"/> should be switched on, given the
        /// active level (-1 = none yet) and whether the active island is parked.
        /// </summary>
        public static bool ShouldBeActive(int levelIndex, int activeLevel, bool parked)
        {
            if (levelIndex < 0) return false;
            if (activeLevel < 0) return false;
            if (parked) return false;
            return levelIndex == activeLevel;
        }

        /// <summary>
        /// The island lists must have one entry per level; the assembler refuses anything
        /// else, because an index that points at the wrong island would hide the level
        /// the player is standing in.
        /// </summary>
        public static bool RootsMatchLevels(int rootCount, int levelCount)
        {
            return rootCount >= 0 && rootCount == levelCount;
        }

        /// <summary>How many islands are on for the given state (0 or 1).</summary>
        public static int ActiveCount(int levelCount, int activeLevel, bool parked)
        {
            int n = 0;
            for (int i = 0; i < levelCount; i++)
            {
                if (ShouldBeActive(i, activeLevel, parked)) n++;
            }
            return n;
        }
    }
}
