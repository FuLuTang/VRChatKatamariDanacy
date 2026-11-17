using NUnit.Framework;
using UnityEngine;
using VRCKatamari.BallContact;

namespace VRCKatamari.Tests
{
    /// <summary>
    /// Test-side view of one contact world: the parallel arrays the solver takes, built
    /// by <see cref="ContactScenes"/>. Runtime code has no struct, so the tests do not
    /// invent one either; they fill the same arrays the motor will own.
    /// </summary>
    internal sealed class ContactWorld
    {
        public const int MaxSolids = 256;

        public bool HasGround = true;
        public Vector3 GroundNormal = new Vector3(0f, 1f, 0f);
        public float GroundOffset;
        public readonly Vector3[] PlaneNormals = new Vector3[0];
        public readonly float[] PlaneOffsets = new float[0];
        public int PlaneCount;
        public int Count;
        public readonly Vector3[] Centers = new Vector3[MaxSolids];
        public readonly Vector3[] AxisX = new Vector3[MaxSolids];
        public readonly Vector3[] AxisY = new Vector3[MaxSolids];
        public readonly Vector3[] AxisZ = new Vector3[MaxSolids];
        public readonly Vector3[] HalfExtents = new Vector3[MaxSolids];
        public readonly float[] BoundingRadii = new float[MaxSolids];
        public readonly int[] SurfaceIds = new int[MaxSolids];
        public readonly int[] Roles = new int[MaxSolids];

        public void Add(Vector3 center, Vector3 axisX, Vector3 axisY, Vector3 axisZ,
            Vector3 halfExtents, int surfaceId, int role)
        {
            Centers[Count] = center;
            AxisX[Count] = axisX;
            AxisY[Count] = axisY;
            AxisZ[Count] = axisZ;
            HalfExtents[Count] = halfExtents;
            BoundingRadii[Count] = ObbContactMath.BoundingRadius(halfExtents);
            SurfaceIds[Count] = surfaceId;
            Roles[Count] = role;
            Count++;
        }
    }

    /// <summary>
    /// One ball's solver state plus its contact log, wired to
    /// <see cref="ContactSolveMath.TryStep"/> the way the motor will wire it.
    /// </summary>
    internal sealed class ContactBall
    {
        public Vector3 Center;
        public float Radius;
        public Vector3 PlanarVelocity;
        public float VerticalVelocity;
        public bool ClimbActive;
        public float ClimbAnchorY;
        public int ClimbSurfaceId = -1;
        public float ClimbCooldown;
        public bool ClimbLocked;
        public float ClimbAlongSpeed;
        public float ClimbTopY;
        public Vector3 ClimbNormal;
        public Vector3 ClimbPoint;

        public readonly Vector3[] ContactNormals =
            new Vector3[ContactTuning.ContactCapacity];
        public readonly Vector3[] ContactPoints = new Vector3[ContactTuning.ContactCapacity];
        public readonly float[] ContactDepths = new float[ContactTuning.ContactCapacity];
        public readonly float[] ContactAngles = new float[ContactTuning.ContactCapacity];
        public readonly int[] ContactSurfaceIds = new int[ContactTuning.ContactCapacity];
        public readonly int[] ContactRoles = new int[ContactTuning.ContactCapacity];
        public readonly int[] ContactFeatures = new int[ContactTuning.ContactCapacity];
        public readonly int[] ContactClasses = new int[ContactTuning.ContactCapacity];

        // The terrain contact source (TERRAIN_CLIMB 6.3). Null means "no terrain": the
        // solver then treats every contact as having no ledge (cap-only climbs).
        public float[] ContactTopY = new float[ContactTuning.ContactCapacity];
        public int[] TerrainCandidates;
        public int TerrainCandidateCount = -1;
        public float[] TriangleMinX;
        public float[] TriangleMaxX;
        public float[] TriangleMinZ;
        public float[] TriangleMaxZ;
        public int[] TriangleSurfaceIds;
        public Vector3[] TerrainVertices;
        public int[] TerrainIndices;
        public int TerrainTriangleCount;
        public Vector3[] ChunkMin;
        public Vector3[] ChunkMax;
        public int[] ChunkStarts;
        public int[] ChunkTriangleCounts;
        public int[] ChunkZoneIds;
        public int ChunkCount;

        // The ledge/top height is a C# addition to the model (TERRAIN_CLIMB 6.3); the Python
        // reference model has no ledge exit yet, so a scenario that must match the model
        // verbatim switches the tops off (contactTopY = null) and behaves cap-only.
        public bool UseContactTops = true;

        public int ContactCount;
        public int DominantSurfaceId;
        public int DominantClass;
        public float ClimbHeight;
        public float ClimbSpeed;
        public int Substeps;
        public float Residual;
        public bool Airborne;
        public Vector3 ReportNormal;

        public bool Step(ContactWorld world, Vector3 driveDirection, float driveSpeed, float dt,
            float driveAccel = 0f)
        {
            return ContactSolveMath.TryStep(world.HasGround, world.GroundNormal,
                world.GroundOffset, world.PlaneNormals, world.PlaneOffsets, world.PlaneCount,
                world.Centers, world.AxisX, world.AxisY, world.AxisZ, world.HalfExtents,
                world.SurfaceIds, world.Roles, world.Count,
                ref Center, Radius, ref PlanarVelocity, ref VerticalVelocity,
                ref ClimbActive, ref ClimbAnchorY, ref ClimbSurfaceId, ref ClimbCooldown,
                ref ClimbLocked, ref ClimbAlongSpeed, ref ClimbTopY, ref ClimbNormal,
                ref ClimbPoint,
                driveDirection, driveSpeed, dt, 0f,
                ContactNormals, ContactPoints, ContactDepths, ContactAngles,
                ContactSurfaceIds, ContactRoles, ContactFeatures, ContactClasses,
                ContactTuning.ContactCapacity,
                out ContactCount, out DominantSurfaceId, out DominantClass, out ClimbHeight,
                out ClimbSpeed, out Substeps, out Residual, out Airborne, out ReportNormal,
                driveAccel: driveAccel,
                contactTopY: UseContactTops ? ContactTopY : null,
                terrainCandidates: TerrainCandidates,
                terrainCandidateCount: TerrainCandidateCount,
                terrainTriangleMinX: TriangleMinX, terrainTriangleMaxX: TriangleMaxX,
                terrainTriangleMinZ: TriangleMinZ, terrainTriangleMaxZ: TriangleMaxZ,
                terrainTriangleSurfaceId: TriangleSurfaceIds,
                terrainVertices: TerrainVertices, terrainIndices: TerrainIndices,
                terrainTriangleCount: TerrainTriangleCount, terrainChunkMin: ChunkMin,
                terrainChunkMax: ChunkMax, terrainChunkStarts: ChunkStarts,
                terrainChunkTriangleCounts: ChunkTriangleCounts,
                terrainChunkZoneIds: ChunkZoneIds, terrainChunkCount: ChunkCount);
        }
    }

    /// <summary>
    /// The worlds the reference model's tests build: the C05 seam, a slope plus deck, a
    /// thick ramp slab approached from the side, and the two walls. Geometry and numbers
    /// are copied from Docs/tests/ball_contact_v2/test_contact_v2.py.
    /// </summary>
    internal static class ContactScenes
    {
        public const float Sin45 = 0.70710678f;
        public const float Cos45 = 0.70710678f;

        /// <summary>C05: a 45 deg slab whose top face meets a 0.6 m deck, 4 m wide.</summary>
        public static ContactWorld SeamWorld()
        {
            Vector3 normal = new Vector3(0f, Cos45, -Sin45);
            Vector3 tangent = new Vector3(0f, Sin45, Cos45);
            Vector3 topMid = new Vector3(0f, 0.3f, 7.9f);
            Vector3 center = new Vector3(topMid.x - normal.x * 0.02f,
                topMid.y - normal.y * 0.02f, topMid.z - normal.z * 0.02f);
            ContactWorld world = new ContactWorld();
            world.Add(center, new Vector3(1f, 0f, 0f), normal, tangent,
                new Vector3(2f, 0.02f, 0.4245f), 10, ContactCodes.RoleSlab);
            world.Add(new Vector3(0f, 0.3f, 8.95f), new Vector3(1f, 0f, 0f),
                new Vector3(0f, 1f, 0f), new Vector3(0f, 0f, 1f),
                new Vector3(2f, 0.3f, 0.75f), 1, ContactCodes.RoleBox);
            return world;
        }

        /// <summary>The same 45 deg top face with a 0.6 m body, for a clean side hit.</summary>
        public static ContactWorld ThickSideSlabWorld()
        {
            Vector3 normal = new Vector3(0f, Cos45, -Sin45);
            Vector3 tangent = new Vector3(0f, Sin45, Cos45);
            Vector3 center = new Vector3(0f, 0.3f - normal.y * 0.3f, 7.9f - normal.z * 0.3f);
            ContactWorld world = new ContactWorld();
            world.Add(center, new Vector3(1f, 0f, 0f), normal, tangent,
                new Vector3(2f, 0.3f, 0.4245f), 10, ContactCodes.RoleSlab);
            return world;
        }

        /// <summary>The seam slab lifted by one metre, with no ground change.</summary>
        public static ContactWorld LiftedSeamSlabWorld()
        {
            ContactWorld seam = SeamWorld();
            ContactWorld world = new ContactWorld();
            world.Add(new Vector3(seam.Centers[0].x, seam.Centers[0].y + 1f,
                seam.Centers[0].z), seam.AxisX[0], seam.AxisY[0], seam.AxisZ[0],
                seam.HalfExtents[0], seam.SurfaceIds[0], seam.Roles[0]);
            return world;
        }

        /// <summary>A slab rising <paramref name="riseM"/> at <paramref name="angleDeg"/>
        /// from a foot at (0, 0, 7), plus a deck whose top is at the rise.</summary>
        public static ContactWorld SlopeWorld(float angleDeg, float riseM, float widthM)
        {
            float angle = angleDeg * Mathf.Deg2Rad;
            Vector3 normal = new Vector3(0f, Mathf.Cos(angle), -Mathf.Sin(angle));
            Vector3 tangent = new Vector3(0f, Mathf.Sin(angle), Mathf.Cos(angle));
            float run = riseM / Mathf.Tan(angle);
            float along = riseM / Mathf.Sin(angle);
            Vector3 mid = new Vector3(0f, riseM * 0.5f, 7f + run * 0.5f);
            ContactWorld world = new ContactWorld();
            world.Add(new Vector3(mid.x - normal.x * 0.05f, mid.y - normal.y * 0.05f,
                mid.z - normal.z * 0.05f), new Vector3(1f, 0f, 0f), normal, tangent,
                new Vector3(widthM * 0.5f, 0.05f, along * 0.5f), 20, ContactCodes.RoleSlab);
            world.Add(new Vector3(0f, riseM - 0.3f, 7f + run + 0.6f), new Vector3(1f, 0f, 0f),
                new Vector3(0f, 1f, 0f), new Vector3(0f, 0f, 1f),
                new Vector3(widthM * 0.5f, 0.3f, 0.6f), 2, ContactCodes.RoleBox);
            return world;
        }

        /// <summary>A 90 deg wall 3 m high in front of the ball (top at y = 3).</summary>
        public static ContactWorld WallWorld()
        {
            ContactWorld world = new ContactWorld();
            world.Add(new Vector3(0f, 1.5f, -0.5f), new Vector3(1f, 0f, 0f),
                new Vector3(0f, 1f, 0f), new Vector3(0f, 0f, 1f),
                new Vector3(5f, 1.5f, 0.5f), 1, ContactCodes.RoleBox);
            return world;
        }

        /// <summary>A 0.5 D low wall (0.15 m for D = 0.30) that must be rolled over.</summary>
        public static ContactWorld LowWallWorld()
        {
            ContactWorld world = new ContactWorld();
            world.Add(new Vector3(0f, 0.075f, -1f), new Vector3(1f, 0f, 0f),
                new Vector3(0f, 1f, 0f), new Vector3(0f, 0f, 1f),
                new Vector3(5f, 0.075f, 1f), 1, ContactCodes.RoleBox);
            return world;
        }
    }

    /// <summary>
    /// EditMode port of the substepping and solid-response cases of
    /// Docs/tests/ball_contact_v2/test_contact_v2.py: the no-tunnel substep bound, a ramp
    /// slab that blocks from the side and from below at the maximum step travel, and the
    /// 45 deg seam that v1 deadlocks on.
    ///
    /// Lengths are metres in world space (+Y up). Expected values are hand calculations or
    /// geometric invariants, never a re-run of the implementation formula; the exact
    /// numbers the Python model produced are in ContactGoldenValueTests.
    /// </summary>
    public class ContactSolveTests
    {
        private const float Tolerance = 1e-4f;

        [Test]
        public void TheSmallestBallAtTopSpeedNeedsTwoSubsteps()
        {
            // 4 m/s * 0.02 s = 0.08 m; r = 0.15 m; kappa 0.5 -> 2 substeps.
            Assert.AreEqual(2, ContactSolveMath.MaxSubSteps(new Vector3(0.08f, 0f, 0f), 0.15f));
            Assert.LessOrEqual(0.08f / 2f, 0.5f * 0.15f);
            // Dash = 1.6x keeps the same count with the margin still under kappa*r.
            Assert.AreEqual(2, ContactSolveMath.MaxSubSteps(new Vector3(0.128f, 0f, 0f), 0.15f));
            Assert.LessOrEqual(0.128f / 2f, 0.5f * 0.15f);
        }

        [Test]
        public void TheNoTunnelBoundLeavesFourTimesTheNeededMargin()
        {
            // The proof only needs a sample spacing <= r; kappa 0.5 gives 4x.
            float[] radii = { 0.15f, 0.30f, 0.55f, 1.10f };
            for (int i = 0; i < radii.Length; i++)
            {
                float travel = 4f * 0.02f;
                int count = ContactSolveMath.MaxSubSteps(new Vector3(travel, 0f, 0f), radii[i]);
                Assert.LessOrEqual(travel / count, radii[i]);
                Assert.LessOrEqual(count, 2);
            }
        }

        [Test]
        public void TheFallDisplacementIsCostCapped()
        {
            Assert.AreEqual(8, ContactSolveMath.MaxSubSteps(new Vector3(0.8f, 0f, 0f), 0.15f));
        }

        [Test]
        public void ARampSideStopsTheBallAtMaxStepTravel()
        {
            ContactWorld world = ContactScenes.ThickSideSlabWorld();
            ContactBall ball = new ContactBall();
            ball.Center = new Vector3(3f, 0.15f, 7.9f);
            ball.Radius = 0.15f;
            ball.PlanarVelocity = new Vector3(-4f, 0f, 0f);
            for (int i = 0; i < 30; i++)
            {
                Assert.IsTrue(ball.Step(world, new Vector3(-1f, 0f, 0f), 4f, 0.02f));
            }
            // The side face is at x = 2; the ball must stay one radius outside it.
            Assert.GreaterOrEqual(ball.Center.x, 2f + 0.15f - 0.002f);
            Assert.LessOrEqual(ball.Residual, 0.002f);
            Assert.GreaterOrEqual(DistanceTo(world, 0, ball.Center), 0.15f - 0.002f);
        }

        [Test]
        public void ASupportOnlyRampWouldPassThroughFromTheSide()
        {
            // Negative control: with the ramp absent from the solids (the v1 model) the
            // same drive walks straight through the board's footprint.
            ContactWorld world = new ContactWorld();
            ContactBall ball = new ContactBall();
            ball.Center = new Vector3(3f, 0.15f, 7.9f);
            ball.Radius = 0.15f;
            ball.PlanarVelocity = new Vector3(-4f, 0f, 0f);
            for (int i = 0; i < 30; i++)
            {
                Assert.IsTrue(ball.Step(world, new Vector3(-1f, 0f, 0f), 4f, 0.02f));
            }
            Assert.Less(ball.Center.x, 1f);
        }

        [Test]
        public void ARampUndersideStopsAnUpwardBall()
        {
            ContactWorld world = ContactScenes.LiftedSeamSlabWorld();
            ContactBall ball = new ContactBall();
            ball.Center = new Vector3(0f, 0.5f, 7.9f);
            ball.Radius = 0.15f;
            ball.VerticalVelocity = 4f;
            float highest = 0f;
            float minimumDistance = 1e9f;
            for (int i = 0; i < 60; i++)
            {
                Assert.IsTrue(ball.Step(world, new Vector3(0f, 0f, 1f), 4f, 0.02f));
                if (ball.Center.y > highest) highest = ball.Center.y;
                float distance = DistanceTo(world, 0, ball.Center);
                if (distance < minimumDistance) minimumDistance = distance;
            }
            // The underside is at y = 1.272 near z = 7.9; the centre must never come out
            // above the slab's top face (y = 1.3), and it never penetrates.
            Assert.Less(highest, 1.30f);
            Assert.GreaterOrEqual(minimumDistance, 0.15f - 0.002f);
        }

        [Test]
        public void A45DegreeSeamReachesTheDeck()
        {
            ContactWorld world = ContactScenes.SeamWorld();
            ContactBall ball = new ContactBall();
            ball.Center = new Vector3(0f, 0.1f + 0.15f * ContactScenes.Sin45,
                7.7f - 0.15f * ContactScenes.Sin45);
            ball.Radius = 0.15f;
            ball.PlanarVelocity = new Vector3(0f, 0f, 4f);
            for (int i = 0; i < 20; i++)
            {
                Assert.IsTrue(ball.Step(world, new Vector3(0f, 0f, 1f), 4f, 0.02f));
            }
            Assert.Greater(ball.Center.z, 8.3f);            // past the deck edge
            Assert.AreEqual(0.75f, ball.Center.y, 0.002f);  // 0.6 + r
            // The v1 inflated-AABB stop point was z = 8.2 - r = 8.05; being past it is
            // exactly what the OBB contact buys.
            Assert.Greater(ball.Center.z, 8.2f - 0.15f);
        }

        [Test]
        public void A45DegreeSeamKeepsThePlanarSpeed()
        {
            ContactWorld world = ContactScenes.SeamWorld();
            ContactBall ball = new ContactBall();
            ball.Center = new Vector3(0f, 0.1f + 0.15f * ContactScenes.Sin45,
                7.7f - 0.15f * ContactScenes.Sin45);
            ball.Radius = 0.15f;
            ball.PlanarVelocity = new Vector3(0f, 0f, 4f);
            for (int i = 0; i < 20; i++)
            {
                Assert.IsTrue(ball.Step(world, new Vector3(0f, 0f, 1f), 4f, 0.02f));
            }
            Assert.AreEqual(4f, ball.PlanarVelocity.z, 0.05f);
            Assert.AreEqual(2, ball.Substeps);
        }

        [Test]
        public void A90DegreeWallClimbsToTheCapThenSlidesBack()
        {
            ContactWorld world = ContactScenes.WallWorld();
            ContactBall ball = new ContactBall();
            ball.Center = new Vector3(0f, 0.30f, 0.5f);
            ball.Radius = 0.30f;
            ball.PlanarVelocity = new Vector3(0f, 0f, -4f);
            float highest = 0f;
            bool sawLocked = false;
            for (int i = 0; i < 250; i++)
            {
                Assert.IsTrue(ball.Step(world, new Vector3(0f, 0f, -1f), 4f, 0.02f));
                if (ball.Center.y > highest) highest = ball.Center.y;
                sawLocked = sawLocked || ball.ClimbLocked;
            }
            float cap = ClimbMath.ClimbHeightCap(0.30f);
            // The first attempt climbs to the face cap (0.30 + 0.88 = 1.18) and peels off;
            // a held push retries from the same anchor (owner D2: "climbs up and drops"), so
            // the peak is bounded by the cap and the end state is only deterministic once the
            // push is released.
            Assert.LessOrEqual(highest - 0.30f, cap + 0.02f);
            Assert.Greater(highest - 0.30f, 0.80f);       // it really did climb to the cap
            Assert.IsTrue(sawLocked);                     // the cap ended the first attempt
            for (int i = 0; i < 300; i++)
            {
                Assert.IsTrue(ball.Step(world, new Vector3(0f, 0f, -1f), 0f, 0.02f));
            }
            Assert.Less(ball.Center.y, 0.32f);            // slid back down
            Assert.IsFalse(ball.ClimbActive);
            // The cap release's peel is C#-only: the rest can sit a few mm off the face, but
            // never inside it.
            Assert.GreaterOrEqual(ball.Center.z, 0.30f - 0.01f);
        }

        [Test]
        public void A70DegreeSlopeIsClimbableButSlower()
        {
            ContactWorld world = ContactScenes.SlopeWorld(70f, 0.24f, 2f);
            ContactBall ball = new ContactBall();
            ball.Center = new Vector3(0f, 0.15f, 6.65f);
            ball.Radius = 0.15f;
            ball.PlanarVelocity = new Vector3(0f, 0f, 4f);
            float highest = 0f;
            for (int i = 0; i < 200; i++)
            {
                Assert.IsTrue(ball.Step(world, new Vector3(0f, 0f, 1f), 4f, 0.02f, 5f));
                if (ball.Center.y > highest) highest = ball.Center.y;
            }
            Assert.AreEqual(0.24f + 0.15f, highest, 0.01f);  // on the 0.24 m deck
            // A rideable 45 deg ramp at the same 4 m/s rises at 4 m/s; the climb is bounded
            // by the climb speed, well under 1 m/s at 70 deg.
            Assert.Less(ClimbMath.ClimbSpeed(1f, 2f, 70f, 0f, 0.3f), 1f);
            Assert.Greater(ClimbMath.ClimbSpeed(1f, 2f, 70f, 0f, 0.3f), 0.2f);
        }

        [Test]
        public void A50DegreeSlabIsClimbedOnlyWithinTheCap()
        {
            // C06 redesigned: 50 deg is in the climb band, and a 0.6 m rise against
            // H_cap = D separates the sizes. D = 0.30 caps at 0.30 and slides back;
            // D = 1.10 reaches the 0.6 deck.
            ContactWorld small = ContactScenes.SlopeWorld(50f, 0.6f, 3f);
            ContactBall smallBall = new ContactBall();
            smallBall.Center = new Vector3(0f, 0.15f, 6.4f);
            smallBall.Radius = 0.15f;
            smallBall.PlanarVelocity = new Vector3(0f, 0f, 4f);
            float smallHighest = 0f;
            bool smallSawLocked = false;
            for (int i = 0; i < 250; i++)
            {
                Assert.IsTrue(smallBall.Step(small, new Vector3(0f, 0f, 1f), 4f, 0.02f, 5f));
                if (smallBall.Center.y > smallHighest) smallHighest = smallBall.Center.y;
                smallSawLocked = smallSawLocked || smallBall.ClimbLocked;
            }
            // The cap is the height above the FACE: the first attempt's anchor is the start
            // centre, so the peak is `anchor + H_cap` (0.15 + 0.45 = 0.60) within the
            // one-substep overshoot, and a retry on the same face keeps that anchor.
            Assert.LessOrEqual(smallHighest - 0.15f, ClimbMath.ClimbHeightCap(0.15f) + 0.02f);
            Assert.Less(smallHighest - 0.15f, 0.6f - 0.05f);  // never the 0.6 m deck
            Assert.IsTrue(smallSawLocked);                    // the cap was reached once
            // A held push retries after the fall (owner D2), so the end state of the cycle is
            // not asserted; releasing is the deterministic form (see the golden test).
            for (int i = 0; i < 200; i++)
            {
                Assert.IsTrue(smallBall.Step(small, new Vector3(0f, 0f, 1f), 0f, 0.02f, 5f));
            }
            Assert.Less(smallBall.Center.y - 0.15f, 0.05f);   // slid back down once released
            Assert.IsFalse(smallBall.ClimbActive);

            ContactWorld big = ContactScenes.SlopeWorld(50f, 0.6f, 3f);
            ContactBall bigBall = new ContactBall();
            bigBall.Center = new Vector3(0f, 0.55f, 6.4f);
            bigBall.Radius = 0.55f;
            bigBall.PlanarVelocity = new Vector3(0f, 0f, 4f);
            float bigHighest = 0f;
            for (int i = 0; i < 250; i++)
            {
                Assert.IsTrue(bigBall.Step(big, new Vector3(0f, 0f, 1f), 4f, 0.02f, 5f));
                if (bigBall.Center.y > bigHighest) bigHighest = bigBall.Center.y;
            }
            Assert.Greater(bigHighest - 0.55f, 0.6f - 0.01f);  // on the 0.6 deck
        }

        [Test]
        public void ALowWallIsRolledOver()
        {
            // 0.5 D wall (0.15 m for D = 0.30): the rounded top edge takes the ball over.
            ContactWorld world = ContactScenes.LowWallWorld();
            ContactBall ball = new ContactBall();
            ball.Center = new Vector3(0f, 0.15f, 0.8f);
            ball.Radius = 0.15f;
            ball.PlanarVelocity = new Vector3(0f, 0f, -4f);
            float highest = 0f;
            for (int i = 0; i < 150; i++)
            {
                Assert.IsTrue(ball.Step(world, new Vector3(0f, 0f, -1f), 4f, 0.02f, 5f));
                if (ball.Center.y > highest) highest = ball.Center.y;
            }
            // The 0.5 D wall's edge normal is CLIMB-class at h = r (n.y = (r-h)/r = 0), so
            // the climb channel also engages on top of the edge roll-over. The ledge exit
            // settles the bottom onto the wall top (<= ClimbLedgeSlopM) and zeroes the
            // climb's vertical speed, so the peak is wallTop + r plus at most the push-out
            // slop: 0.15 + 0.15 + 0.005. (The Python model has no ledge exit and peaks at
            // exactly 0.30 - see the golden test's note.)
            Assert.GreaterOrEqual(highest, 0.30f - 0.01f);
            Assert.LessOrEqual(highest, 0.15f + 0.15f + ContactTuning.ClimbLedgeSlopM);
            Assert.Less(ball.Center.z, -2f);         // crossed to the far side
        }

        [Test]
        public void TheHeightCapIsOneAndAHalfDiametersAndAddsProtrusion()
        {
            // k_h = 1.5 (owner 2026-11-16, PROVISIONAL): H_cap = 1.5 * (2r + L). The old
            // 2r (k_h = 1) contract is gone with the constant.
            Assert.AreEqual(0.45f, ClimbMath.ClimbHeightCap(0.15f), Tolerance);
            Assert.AreEqual(0.90f, ClimbMath.ClimbHeightCap(0.30f), Tolerance);
            Assert.AreEqual(1.65f, ClimbMath.ClimbHeightCap(0.55f), Tolerance);
            Assert.AreEqual(3.30f, ClimbMath.ClimbHeightCap(1.10f), Tolerance);
            Assert.AreEqual(1.20f, ClimbMath.ClimbHeightCap(0.30f, 0.2f), Tolerance);
        }

        [Test]
        public void TheGateIsContinuousAndSteeperMeansMoreSpeed()
        {
            Assert.Less(ClimbMath.RequiredSpeed(50f), ClimbMath.RequiredSpeed(70f));
            Assert.Less(ClimbMath.RequiredSpeed(70f), ClimbMath.RequiredSpeed(90f));
            Assert.AreEqual(1.5f, ClimbMath.RequiredSpeed(90f), Tolerance);
            Assert.Less(ClimbMath.ClimbSpeed(1f, 2f, 70f, 0f, 0.6f),
                ClimbMath.ClimbSpeed(1f, 2f, 50f, 0f, 0.6f));
            Assert.Less(ClimbMath.ClimbSpeed(1f, 2f, 90f, 0f, 0.6f), 0.40f);
            // A light push and a cap that is already reached both give no climb at all.
            Assert.AreEqual(0f, ClimbMath.ClimbSpeed(0f, 2f, 70f, 0f, 0.6f), Tolerance);
            Assert.AreEqual(0f, ClimbMath.ClimbSpeed(1f, 2f, 70f, 0.6f, 0.6f), Tolerance);
        }

        private static float DistanceTo(ContactWorld world, int index, Vector3 point)
        {
            Vector3 closestPoint;
            float distance;
            Vector3 normal;
            int feature;
            Assert.IsTrue(ObbContactMath.TryClosestPointOnObb(world.Centers[index],
                world.AxisX[index], world.AxisY[index], world.AxisZ[index],
                world.HalfExtents[index], point, out closestPoint, out distance, out normal,
                out feature));
            return distance;
        }
    }
}
