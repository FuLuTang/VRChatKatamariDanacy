namespace VRCKatamari.Story
{
    /// <summary>
    /// The <c>[story]</c> line's support vocabulary, in one place.
    /// </summary>
    /// <remarks>
    /// A support KIND is not a surface index. The table knows ground / box / ramp / prop /
    /// terrain, plus two kinds of the contact model: 4 = the ball is held by
    /// a climb contact (a wall with no floor) and 5 = only a pin is holding it up
    /// (<c>ContactModelV2</c> 2.5). The line printed <c>none</c> for both, which read as
    /// "no support" while the ball was visibly climbing - a display gap, not physics.
    /// </remarks>
    public static class BallDiagnosticsMath
    {
        /// <summary>The name the line prints for a support kind (0..6, else "none").</summary>
        public static string SupportName(int supportKind)
        {
            if (supportKind == 0) return "ground";
            if (supportKind == 1) return "box";
            if (supportKind == 2) return "ramp";
            if (supportKind == 3) return "prop";
            if (supportKind == 4) return "climb";
            if (supportKind == 5) return "pin";
            if (supportKind == 6) return "terrain";
            return "none";
        }

        /// <summary>
        /// True when the motor reports a support at all (a surface, a climb contact or a pin).
        /// </summary>
        /// <remarks>
        /// A false answer means the support pair is STALE (the motor keeps the previous
        /// point/normal when nothing supports the ball), so the line must not print a height
        /// or a gap as if they belonged to this frame.
        /// </remarks>
        public static bool HasSupport(int supportKind)
        {
            return supportKind >= 0;
        }
    }
}
