namespace VRCKatamari.BallContact
{
    /// <summary>
    /// The small integer codes the ball-contact v2 record arrays carry.
    /// </summary>
    /// <remarks>
    /// Plain constants rather than enums: the codes are stored in <c>int</c> arrays
    /// next to the contact records and compared with the reference model's numbers
    /// (0/1/2/3), so a cast-free constant keeps the arrays and the Python model
    /// readable side by side. Only <see cref="ContactCodes.ClassNone"/> and friends
    /// select behaviour; <c>Feature*</c> and <c>Role*</c> are diagnostics and author
    /// semantics, exactly as the design says (section 2.1).
    /// </remarks>
    public static class ContactCodes
    {
        // Feature of the closest point on a box (diagnostics and tests only).
        /// <summary>The query point is inside the solid.</summary>
        public const int FeatureInside = 0;

        /// <summary>The closest point lies on one face.</summary>
        public const int FeatureFace = 1;

        /// <summary>The closest point lies on one edge.</summary>
        public const int FeatureEdge = 2;

        /// <summary>The closest point is one corner.</summary>
        public const int FeatureCorner = 3;

        // Solid role. Every role is the same OBB primitive; the role only labels it.
        /// <summary>A static or prop box on the world axes or on its own axes.</summary>
        public const int RoleBox = 0;

        /// <summary>A ramp slab: the walking face is +local Y.</summary>
        public const int RoleSlab = 1;

        /// <summary>A support prop.</summary>
        public const int RoleProp = 2;

        /// <summary>A blocking prop or a boundary plane.</summary>
        public const int RoleBlocking = 3;

        // Contact class, derived from theta. It selects the velocity response.
        /// <summary>Ceiling or underside: blocks in three dimensions, no ride, no climb.</summary>
        public const int ClassNone = 0;

        /// <summary>Rideable: the ball rolls on it and no speed component is removed.</summary>
        public const int ClassRide = 1;

        /// <summary>Climbable/blocking: the climb channel owns the velocity, or inward speed is removed.</summary>
        public const int ClassClimb = 2;

        /// <summary>The ground plane's contact surface id.</summary>
        public const int SurfaceIdGround = 0;

        /// <summary>Boundary plane <paramref name="planeIndex"/> uses 1000 + index.</summary>
        public static int PlaneSurfaceId(int planeIndex)
        {
            return 1000 + planeIndex;
        }
    }
}
