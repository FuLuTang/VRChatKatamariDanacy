namespace VRCKatamari.BallContact
{
    /// <summary>
    /// Every number the ball-contact v2 library consumes, in one place.
    /// </summary>
    /// <remarks>
    /// <para>
    /// PROVISIONAL. These are OUR design values, taken from
    /// Docs/Design/BallMotor/CONTACT_MODEL_V2.md (mostly section 3.5) and mirrored
    /// name for name by the Python reference model
    /// Docs/Design/BallMotor/contact_v2/contact_v2.py and shapes.py. They are not
    /// measured values of the original game and none of them has been play-tested.
    /// </para>
    /// <para>
    /// NO value here is an original-game constant. The cross-series check
    /// (Docs/Research/KatamariBallRules/ORIGINAL_CLIMB_OBSERVATIONS.md, 2026-11-16)
    /// found no source for a normal-wall height cap, a climb rate or a time-to-top in
    /// the first game or in REROLL: the manual paraphrases only say the ball can climb
    /// a wall to reach a higher area (no size ratio), the "1-2x ball height" numbers are
    /// weak, mutually contradictory sequel/community claims, and the one concrete
    /// long-climb anecdote (the factory chimney) is a LADDER, not a wall. So
    /// <see cref="KHeight"/> = 1.5 and <see cref="ClimbTimeS"/> = 4.5 s are OUR
    /// PROVISIONAL values (owner decision 2026-11-16), chosen so the reference level's
    /// 0.222 m ball can clear its 0.19 m spawn pit and the observed ~0.30 m step
    /// (Docs/Design/BallMotor/TERRAIN_CLIMB.md section 7), and are to be tuned in
    /// play-testing. Every other number - angles, speeds, caps, margins, capacities -
    /// is likewise ours.
    /// </para>
    /// <para>
    /// UdonSharp forbids static fields, so everything is a compile-time constant and
    /// there is no mutable tuning state. Values are floats: the reference model runs
    /// in double, so golden-value tests compare with a documented tolerance instead
    /// of bit equality.
    /// </para>
    /// </remarks>
    public static class ContactTuning
    {
        // ---- Contact classification (section 2.2) ----

        /// <summary>theta_ride = 45 deg: at or below this the ball rides the surface.</summary>
        public const float ThetaRideDeg = 45f;

        /// <summary>
        /// 0.5 deg of equality tolerance on the ride boundary: a 45 deg board sits
        /// exactly on the line and float noise must not flip it between ride and climb.
        /// </summary>
        public const float ThetaRideToleranceDeg = 0.5f;

        /// <summary>
        /// 15 deg: a climb contact must be clearly steeper than the surface the ball is
        /// rolling on, otherwise it is that surface curving over and the push-out rolls
        /// the ball over it instead of the climb channel taking the velocity.
        /// </summary>
        public const float ClimbSteeperMarginDeg = 15f;

        /// <summary>
        /// A normal whose +Y part is below -this is a ceiling/underside (CLASS_NONE):
        /// it blocks in three dimensions, it is neither rideable nor climbable.
        /// </summary>
        public const float MinUpwardNormalY = 0.05f;

        /// <summary>
        /// Distance equality tolerance of the support/climb tie-breaks: a float-scale
        /// placeholder for the model's 1e-9 double epsilon.
        /// </summary>
        public const float TieTolerance = 1e-6f;

        // ---- Contact solving (sections 1.7, 2.3) ----

        /// <summary>kappa: the maximum travel of one substep, in ball radii.</summary>
        public const float KappaSubstep = 0.5f;

        /// <summary>N_max: the substep cost cap; the fall displacement is clamped to it.</summary>
        public const int MaxSubsteps = 8;

        /// <summary>Sequential push-out passes over the deepest contact.</summary>
        public const int SolverIterations = 3;

        /// <summary>0.5 mm: contacts shallower than this are not pushed out (no jitter).</summary>
        public const float ContactSlopM = 0.0005f;

        /// <summary>
        /// 5 mm: how far outside the sphere a contact stays registered. A registered
        /// skin contact has depth &lt;= 0 and never pushes; it is "near", not "touching".
        /// </summary>
        public const float ContactSkinM = 0.005f;

        /// <summary>Fixed contact record capacity per query (section 1.3 / 4A.6).</summary>
        public const int ContactCapacity = 12;

        /// <summary>Fall-speed clamp the model writes as a literal (-40 m/s).</summary>
        public const float MaxFallSpeedMps = 40f;

        // ---- Climb (sections 3.2, 3.3, 3.5) ----

        /// <summary>p_min: a push-in weaker than this never climbs and never keeps a climb.</summary>
        public const float PMin = 0.2f;

        /// <summary>v_min: the drive speed the 90 deg velocity gate requires.</summary>
        public const float VMinGate = 1.5f;

        /// <summary>alpha: v_req = v_min * sin(theta)^alpha.</summary>
        public const float GateAlpha = 1f;

        /// <summary>k_intent: the motion layer's target speed counts half as push-in speed.</summary>
        public const float KIntent = 0.5f;

        /// <summary>T_climb = 4.5 s: the time every ball size takes to reach H_cap.</summary>
        public const float ClimbTimeS = 4.5f;

        /// <summary>
        /// 1.718 = 1 / (1 - sqrt(0.02)): the sqrt-fade normalisation, i.e. v_up0 is the
        /// initial rate whose fade reaches 98% of H_cap after T_climb.
        /// </summary>
        public const float ClimbTimeNorm = 1.718f;

        /// <summary>The gate speed that gives climb rate factor 1.0.</summary>
        public const float ClimbVGateRef = 2f;

        /// <summary>Lower clamp of the climb rate factor (a light push still climbs slowly).</summary>
        public const float ClimbRateMin = 0.75f;

        /// <summary>Upper clamp of the climb rate factor (dash reaches it, nothing exceeds it).</summary>
        public const float ClimbRateMax = 1.5f;

        /// <summary>m0 = 0.15: the creep floor in m(theta) = cos(theta) + m0 * sin(theta).</summary>
        public const float M0 = 0.15f;

        /// <summary>
        /// k_h = 1.5: H_cap = k_h * (2r + lambda * L_eff). OUR PROVISIONAL value (no
        /// original-game cap is published; see the class remarks). It sits inside the
        /// weakest community range (1-2x ball height) and is the smallest value that
        /// clears the 0.222 m ball's 0.30 m step with the 0.02 m cap margin
        /// (TERRAIN_CLIMB section 7); owner decision 2026-11-16, tune in play-testing.
        /// </summary>
        public const float KHeight = 1.5f;

        /// <summary>lambda = 1.0: how much a protruding object adds to H_cap.</summary>
        public const float LambdaProtrusion = 1f;

        /// <summary>a_climb = 3 m/s^2: the climb speed may change by at most this per second.</summary>
        public const float ClimbRateLimit = 3f;

        /// <summary>Cooldown after leaving a climb (release or cap), in seconds.</summary>
        public const float ClimbCooldownS = 0.35f;

        /// <summary>cap_margin = 0.02 m: the band below H_cap where the climb is done.</summary>
        public const float ClimbCapMarginM = 0.02f;

        /// <summary>
        /// 5 mm: the ball's bottom counts as "on the ledge" within this of the climb
        /// surface's top, which is the successful way out of a climb (TERRAIN_CLIMB 6.3).
        /// </summary>
        public const float ClimbLedgeSlopM = 0.005f;

        /// <summary>
        /// 0.3 m/s: the outward speed a cap release gives along the wall normal. It makes
        /// "too tall" read as "thrown off" and stops the ball hovering at the cap; a
        /// release by the player does NOT peel (the ball slides down instead).
        /// </summary>
        public const float ClimbPeelSpeedMps = 0.3f;

        /// <summary>
        /// 10 per second: the ledge exit speed is <c>ClimbMath.LedgeExitSpeed</c> =
        /// clamp(10 * radius, min, driveSpeed) m/s. t = r / v and drop = g t^2 / 2, so a
        /// 5 cm drop budget gives v = r * sqrt(9.81 / 0.1) = 9.9 * r. Units: 1/s times m =
        /// m/s (for r = 0.111 that is 1.11 m/s, not 10 / 0.111).
        /// </summary>
        public const float ClimbLedgeDrivePerRadius = 10f;

        /// <summary>Floor of the ledge exit speed, m/s (a full push always clears).</summary>
        public const float ClimbLedgeDriveMinMps = 0.5f;

        /// <summary>
        /// 3 cm: how far the ball may drift off the climb plane while the climb is still
        /// alive (the last radius above a low face has no contact; this keeps the hang tied
        /// to the wall it started on instead of lifting anywhere).
        /// </summary>
        public const float ClimbHugSlopM = 0.03f;

        /// <summary>
        /// 0.99: two climb contacts are the SAME face (one plane) when their normals agree
        /// to this dot product and the plane distance is within
        /// <see cref="ClimbFacePlaneM"/>. Used only by the selection hysteresis: while a
        /// climb is alive its face keeps being the face the solver reads
        /// (TERRAIN_CLIMB 6.6). It is NOT an anchor identity any more.
        /// </summary>
        public const float ClimbFaceNormalDot = 0.99f;

        /// <summary>2 cm: the plane distance of "the same face" (see <see cref="ClimbFaceNormalDot"/>).</summary>
        public const float ClimbFacePlaneM = 0.02f;

        /// <summary>v_slide_max = 1.0 m/s: the slow slide back down a touched wall.</summary>
        public const float VSlideMax = 1f;

        /// <summary>
        /// The smallest sin(theta) used to turn a vertical climb rate into a rate along
        /// the uphill tangent; 90 deg stays a well-defined finite number.
        /// </summary>
        public const float MinClimbSinTheta = 0.2f;

        /// <summary>The project's gravity, the same value the motor integrates.</summary>
        public const float Gravity = 9.81f;

        // ---- Dash (section 3.8, owner decision 4) ----

        /// <summary>Dash speed multiplier on the motion layer's target speed.</summary>
        public const float DashSpeedScale = 1.6f;

        /// <summary>Dash duration in seconds.</summary>
        public const float DashSeconds = 0.45f;

        /// <summary>Dash cooldown in seconds.</summary>
        public const float DashCooldownS = 1.2f;

        // ---- Broadphase window (sections 5.2, 5.4, owner decision 12) ----

        /// <summary>Static OBB table capacity (map 0 needs about 118; one doubling of margin).</summary>
        public const int MaxStaticObbCount = 256;

        /// <summary>Per-step near-set capacity.</summary>
        public const int MaxNearObbCount = 24;

        /// <summary>enter_margin: how far outside r + travel an item still enters the window.</summary>
        public const float WindowEnterMarginM = 0.05f;

        /// <summary>exit_margin: how much further away an already-selected item must move to leave.</summary>
        public const float WindowExitMarginM = 0.5f;

        /// <summary>0.02 m: a dropped item closer than r + this could have been touching.</summary>
        public const float WindowCompleteMarginM = 0.02f;

        // ---- Shapes (section 4A, owner decisions 13, 14, 15) ----

        /// <summary>r_visual = 0.95 * r_phys: the visual core is slightly inside the physical sphere.</summary>
        public const float VisualCoreToPhysical = 0.95f;

        /// <summary>Maximum physical pin protrusion per core radius (matches the radial proxy cap).</summary>
        public const float NeedleProtrusionPerRadius = 0.35f;

        /// <summary>Absolute cap on a pin's physical protrusion, in metres.</summary>
        public const float NeedleProtrusionWorldM = 0.3f;

        /// <summary>r_tip = 0.01 m: the collision radius of a needle tip.</summary>
        public const float NeedleTipRadiusM = 0.01f;

        /// <summary>The patent's 16 fixed pin directions.</summary>
        public const int NeedleDirectionCount = 16;

        /// <summary>The 4 optional extra pins, reported separately.</summary>
        public const int OptionalNeedleCount = 4;

        /// <summary>At most this many protruding pins enter the contact set.</summary>
        public const int CompactNeedleCap = 8;

        /// <summary>A 1 mm protrusion over the core is the threshold for "protruding".</summary>
        public const float NeedleProtrusionThresholdM = 0.001f;

        /// <summary>k_grip = 0.5: a half-radius protrusion halves the climb speed gate.</summary>
        public const float NeedleSteeperGrip = 0.5f;

        /// <summary>max_error_per_radius = 0.05: the curved-ramp chord budget, as a fraction of r.</summary>
        public const float CurveMaxChordErrorPerRadius = 0.05f;

        /// <summary>
        /// 1e-3: the largest |dot(axis_i, axis_j)| a baked box may have. Above it the
        /// corners came from a sheared transform (non-uniform parent scale times rotation),
        /// and the arena refuses the OBB and falls back to the bounds AABB with a warning
        /// rather than querying a box the author did not draw.
        /// </summary>
        public const float BakeShearTolerance = 0.001f;

        // ---- Solid roles / contact classes (diagnostics only, section 2.1) ----
        // These live in ContactCodes: they are identifiers, not tunable numbers.
    }
}
