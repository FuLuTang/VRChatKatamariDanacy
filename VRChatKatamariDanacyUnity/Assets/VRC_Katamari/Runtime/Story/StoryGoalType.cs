namespace VRCKatamari.Story
{
    /// <summary>
    /// Which quest a story level sets: a size goal or a category count goal.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One value per story quest type that has a level:
    /// reach_target_diameter is <see cref="ReachTargetDiameter"/>,
    /// collect_category_count is <see cref="CollectCategoryCount"/>
    /// (Docs/Design/GameModes/README.md sections 2.1 and 4.3). The third quest type
    /// the design lists, absorb_specific_target, is deliberately absent: no level
    /// uses it, and the map has no unique target instance to name
    /// (README section 6-B6). A level sets exactly one quest type.
    /// </para>
    /// <para>
    /// Why it is its own file. The round (StoryRoundController) reads it to pick which
    /// HUD line and which measurement it reports, and the optional result presenter
    /// reads it through the round to pick which finished measurement to judge. It is
    /// level vocabulary both sides name, not arithmetic, so it belongs to neither the
    /// clock nor the star table and moving either one must not move it.
    /// </para>
    /// </remarks>
    public enum StoryGoalType
    {
        ReachTargetDiameter = 0,
        CollectCategoryCount = 1
    }
}
