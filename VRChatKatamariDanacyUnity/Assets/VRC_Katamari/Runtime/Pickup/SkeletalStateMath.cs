namespace VRCKatamari.Pickup
{
    /// <summary>
    /// The original game's animation states of a skeletal prop, and the original simulation's
    /// animation number of each. The skeletal FBX takes and the generated Animator states carry
    /// these names (Docs/Design/PickupAbsorb/MOTION_HOST.md, skeletal animation section).
    /// </summary>
    /// <remarks>
    /// Source: REROLL (2019) <c>resources.assets</c>, table <c>mononame</c>, column
    /// <c>ANIM_NAME</c>; the managed side plays <c>ANIM_NAME[GetPropAnimationNo(...)]</c>, so the
    /// index is the native simulation's animation number (<c>playAnimNo</c> in the original dump).
    /// For all 18 skeletal types of the Cancer level the order is Idle 0, Walk 1, Rampage 2,
    /// Escape 3, Chase 4, Special 5 (the snoozing mother's index 0 reads <c>Snoose</c> and is her
    /// idle). This is NOT the PS2 clip-slot order (walk, escape, rampage, idle, chase, special):
    /// never use an animation number as a slot index.
    /// </remarks>
    public static class SkeletalStateMath
    {
        public const string Idle = "idle";
        public const string Walk = "walk";
        public const string Rampage = "rampage";
        public const string Escape = "escape";
        public const string Chase = "chase";
        public const string Special = "special";

        /// <summary>Number of original animation numbers (0..5).</summary>
        public const int AnimNoCount = 6;

        /// <summary>
        /// Our state name for an original animation number (<c>playAnimNo</c>), or <c>""</c> when the
        /// number is outside 0..5.
        /// </summary>
        public static string StateOfAnimNo(int animNo)
        {
            switch (animNo)
            {
                case 0: return Idle;
                case 1: return Walk;
                case 2: return Rampage;
                case 3: return Escape;
                case 4: return Chase;
                case 5: return Special;
                default: return "";
            }
        }
    }
}
