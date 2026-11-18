using VRCKatamari.Core;

namespace VRCKatamari.Hub
{
    /// <summary>
    /// Which maximum ball diameter a level gets: the per-level entry when the table has a
    /// usable one, otherwise the scene's authored cap.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Owner 2026-11-18: the size cap is a per-level parameter, as in the original where
    /// MAS1 caps at 30 cm and Cancer at 1.2 m. A multi-level scene therefore needs one cap
    /// per level, and <see cref="LevelSwitchHost"/> writes the resolved value to the motor
    /// before it resets the round.
    /// </para>
    /// <para>
    /// The fallback is the motor's AUTHORED cap (the value the scene serialized, captured
    /// before the first switch), not the cap the previous level left behind: keeping the
    /// previous level's cap would make the result depend on the order the player visits the
    /// levels, and a player switching away from a small-cap level would carry that cap into
    /// a level that never asked for one. The missing-entry case is a scene written before
    /// the per-level table existed, so "the scene's own cap" is exactly what it used to get.
    /// </para>
    /// </remarks>
    public static class LevelBallCapMath
    {
        /// <summary>
        /// <paramref name="diameterM"/> is a usable cap: a finite, positive number of metres.
        /// </summary>
        public static bool IsUsable(float diameterM)
        {
            return KatamariMath.IsFiniteFloat(diameterM) && diameterM > 0f;
        }

        /// <summary>
        /// The cap for <paramref name="levelIndex"/>: the per-level entry when it exists and
        /// is usable, otherwise <paramref name="sceneDefaultM"/>.
        /// </summary>
        public static float Resolve(float[] maxDiametersM, int levelIndex, float sceneDefaultM)
        {
            if (maxDiametersM != null && levelIndex >= 0 && levelIndex < maxDiametersM.Length)
            {
                float value = maxDiametersM[levelIndex];
                if (IsUsable(value)) return value;
            }
            return sceneDefaultM;
        }
    }
}
