using NUnit.Framework;
using UnityEngine;
using VRCKatamari.BallContact;
using VRCKatamari.Core;
using VRCKatamari.Motion;

namespace VRCKatamari.Tests
{
    /// <summary>
    /// A real triangle mesh in the shape the motor hands the solver: world vertices, a
    /// triangle list, zones, and the grid's per-triangle XZ boxes and surface ids.
    /// </summary>
    internal sealed class TerrainFixture
    {
        public const int FirstSurfaceId = TerrainMeshMath.FirstTerrainSurfaceId;

        public Vector3[] Vertices = new Vector3[256];
        public int[] Indices = new int[384];
        public int TriangleCount;
        public int VertexCount;
        public float[] MinX = new float[128];
        public float[] MaxX = new float[128];
        public float[] MinZ = new float[128];
        public float[] MaxZ = new float[128];
        public int[] SurfaceIds = new int[128];
        public int[] Candidates = new int[128];
        public int CandidateCount;

        /// <summary>One outward quad; the winding is fixed up so the normal is the front side.</summary>
        public void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 normal)
        {
            if (Vector3.Dot(Vector3.Cross(b - a, c - a), normal) < 0f)
            {
                Vector3 swap = b;
                b = d;
                d = swap;
            }
            int baseIndex = VertexCount;
            Vertices[VertexCount] = a;
            Vertices[VertexCount + 1] = b;
            Vertices[VertexCount + 2] = c;
            Vertices[VertexCount + 3] = d;
            VertexCount += 4;
            AddTriangle(baseIndex, baseIndex + 1, baseIndex + 2);
            AddTriangle(baseIndex, baseIndex + 2, baseIndex + 3);
        }

        private void AddTriangle(int a, int b, int c)
        {
            int at = TriangleCount * 3;
            Indices[at] = a;
            Indices[at + 1] = b;
            Indices[at + 2] = c;
            Vector3 va = Vertices[a];
            Vector3 vb = Vertices[b];
            Vector3 vc = Vertices[c];
            MinX[TriangleCount] = Mathf.Min(va.x, Mathf.Min(vb.x, vc.x));
            MaxX[TriangleCount] = Mathf.Max(va.x, Mathf.Max(vb.x, vc.x));
            MinZ[TriangleCount] = Mathf.Min(va.z, Mathf.Min(vb.z, vc.z));
            MaxZ[TriangleCount] = Mathf.Max(va.z, Mathf.Max(vb.z, vc.z));
            SurfaceIds[TriangleCount] = FirstSurfaceId;
            Candidates[CandidateCount] = TriangleCount;
            CandidateCount++;
            TriangleCount++;
        }

        /// <summary>A flat floor 4 x 4 m at y = 0.</summary>
        public static TerrainFixture Floor()
        {
            TerrainFixture mesh = new TerrainFixture();
            mesh.Quad(new Vector3(-2f, 0f, 0f), new Vector3(2f, 0f, 0f),
                new Vector3(2f, 0f, 4f), new Vector3(-2f, 0f, 4f), new Vector3(0f, 1f, 0f));
            return mesh;
        }

        /// <summary>Floor plus a vertical riser at z = 2 and (optionally) a top face.</summary>
        public static TerrainFixture Step(float riserHeight, bool withTop)
        {
            TerrainFixture mesh = new TerrainFixture();
            mesh.Quad(new Vector3(-2f, 0f, 0f), new Vector3(2f, 0f, 0f),
                new Vector3(2f, 0f, 2f), new Vector3(-2f, 0f, 2f), new Vector3(0f, 1f, 0f));
            mesh.Quad(new Vector3(-2f, 0f, 2f), new Vector3(2f, 0f, 2f),
                new Vector3(2f, riserHeight, 2f), new Vector3(-2f, riserHeight, 2f),
                new Vector3(0f, 0f, -1f));
            if (withTop)
            {
                mesh.Quad(new Vector3(-2f, riserHeight, 2f), new Vector3(2f, riserHeight, 2f),
                    new Vector3(2f, riserHeight, 12f), new Vector3(-2f, riserHeight, 12f),
                    new Vector3(0f, 1f, 0f));
            }
            return mesh;
        }

        /// <summary>
        /// Floor plus the same 0.19 m riser split into two stacked quads at
        /// <paramref name="splitAt"/>: the coarse-mesh triangulation a real import may have.
        /// </summary>
        public static TerrainFixture SplitRiser(float riserHeight, float splitAt)
        {
            TerrainFixture mesh = new TerrainFixture();
            mesh.Quad(new Vector3(-2f, 0f, 0f), new Vector3(2f, 0f, 0f),
                new Vector3(2f, 0f, 2f), new Vector3(-2f, 0f, 2f), new Vector3(0f, 1f, 0f));
            mesh.Quad(new Vector3(-2f, 0f, 2f), new Vector3(2f, 0f, 2f),
                new Vector3(2f, splitAt, 2f), new Vector3(-2f, splitAt, 2f),
                new Vector3(0f, 0f, -1f));
            mesh.Quad(new Vector3(-2f, splitAt, 2f), new Vector3(2f, splitAt, 2f),
                new Vector3(2f, riserHeight, 2f), new Vector3(-2f, riserHeight, 2f),
                new Vector3(0f, 0f, -1f));
            mesh.Quad(new Vector3(-2f, riserHeight, 2f), new Vector3(2f, riserHeight, 2f),
                new Vector3(2f, riserHeight, 12f), new Vector3(-2f, riserHeight, 12f),
                new Vector3(0f, 1f, 0f));
            return mesh;
        }

        /// <summary>Floor, a vertical riser up to (top - bevel), a 45 deg bevel, then the top.</summary>
        public static TerrainFixture ChamferedStep(float top, float bevel)
        {
            float bevelStart = top - bevel;
            float z0 = 2f;
            float z1 = z0 + bevel;
            TerrainFixture mesh = new TerrainFixture();
            mesh.Quad(new Vector3(-2f, 0f, 0f), new Vector3(2f, 0f, 0f),
                new Vector3(2f, 0f, z0), new Vector3(-2f, 0f, z0), new Vector3(0f, 1f, 0f));
            mesh.Quad(new Vector3(-2f, 0f, z0), new Vector3(2f, 0f, z0),
                new Vector3(2f, bevelStart, z0), new Vector3(-2f, bevelStart, z0),
                new Vector3(0f, 0f, -1f));
            float diagonal = 1f / Mathf.Sqrt(2f);
            mesh.Quad(new Vector3(-2f, bevelStart, z0), new Vector3(2f, bevelStart, z0),
                new Vector3(2f, top, z1), new Vector3(-2f, top, z1),
                new Vector3(0f, diagonal, -diagonal));
            mesh.Quad(new Vector3(-2f, top, z1), new Vector3(2f, top, z1),
                new Vector3(2f, top, 12f), new Vector3(-2f, top, 12f), new Vector3(0f, 1f, 0f));
            return mesh;
        }

        /// <summary>Bind the mesh to a ball's terrain contact arrays.</summary>
        public void Bind(ContactBall ball)
        {
            ball.TerrainVertices = Vertices;
            ball.TerrainIndices = Indices;
            ball.TerrainTriangleCount = TriangleCount;
            ball.TerrainCandidates = Candidates;
            ball.TerrainCandidateCount = CandidateCount;
            ball.TriangleMinX = MinX;
            ball.TriangleMaxX = MaxX;
            ball.TriangleMinZ = MinZ;
            ball.TriangleMaxZ = MaxZ;
            ball.TriangleSurfaceIds = SurfaceIds;
        }
    }

    /// <summary>
    /// The motor's per-step order for a core-only ball (no needles, no stuck recovery),
    /// as pure math: <c>BallMotionMath.TryStep</c> (the motion layer's velocity), the
    /// motor's <c>ComputeDrive</c> (the drive direction is the VELOCITY direction, the
    /// facing only when at rest), <c>RefreshClimbGate</c> (the standing climb gate, read
    /// against the solver's remembered climb face) and <c>ContactSolveMath.TryStep</c>.
    /// The Unity-side twin of every test that uses it is in ContactModelV2Tests, which
    /// runs the real motor.
    /// </summary>
    internal sealed class ClimbMotorLoop
    {
        public const float Dt = 0.02f;
        public readonly ContactBall Ball = new ContactBall();
        public readonly ContactWorld World;
        public float HeadingDegrees;
        public float Forward = 1f;
        public float MaxSpeedMps = 4f;
        public float HighestCenterY;
        public int Steps;

        public ClimbMotorLoop(ContactWorld world, Vector3 center, float radius,
            float headingDegrees)
        {
            World = world;
            Ball.Center = center;
            Ball.Radius = radius;
            Ball.ClimbSurfaceId = -1;
            HeadingDegrees = headingDegrees;
            HighestCenterY = center.y;
        }

        public bool Step()
        {
            float nextHeading;
            Vector3 nextVelocity;
            Vector3 displacement;
            if (BallMotionMath.TryStep(Forward, 0f, 0f, HeadingDegrees, Ball.PlanarVelocity,
                Dt, 1f, MaxSpeedMps, 7.5f, 6f, 0.9f, 1.3f, 2f, 110f, 1.2f,
                out nextHeading,
                out nextVelocity, out displacement))
            {
                HeadingDegrees = nextHeading;
                Ball.PlanarVelocity = new Vector3(nextVelocity.x, 0f, nextVelocity.z);
            }

            // ComputeDrive: the way the ball MOVES, else the way it faces.
            Vector3 planar = new Vector3(Ball.PlanarVelocity.x, 0f, Ball.PlanarVelocity.z);
            Vector3 direction;
            float length;
            if (!KatamariMath.TryNormalize(planar, out direction, out length))
            {
                float radians = HeadingDegrees * Mathf.Deg2Rad;
                direction = new Vector3(Mathf.Sin(radians), 0f, Mathf.Cos(radians));
            }
            float driveSpeed = MaxSpeedMps * Mathf.Abs(Forward);

            // RefreshClimbGate: a climb whose push or gate speed has fallen leaves the channel.
            if (Ball.ClimbActive)
            {
                Vector3 normal = Ball.ClimbNormal;
                float horizontal = Mathf.Sqrt(normal.x * normal.x + normal.z * normal.z);
                if (horizontal > 0f)
                {
                    float pushIn = Mathf.Max(0f,
                        -(direction.x * normal.x / horizontal + direction.z * normal.z / horizontal));
                    float speed = Mathf.Sqrt(Ball.PlanarVelocity.x * Ball.PlanarVelocity.x
                        + Ball.PlanarVelocity.z * Ball.PlanarVelocity.z);
                    float vGate = Mathf.Max(speed, ContactTuning.KIntent * driveSpeed);
                    if (!(pushIn >= ContactTuning.PMin &&
                        vGate >= ClimbMath.RequiredSpeed(ObbContactMath.ContactAngleDeg(normal))))
                    {
                        Ball.ClimbActive = false;
                        Ball.ClimbCooldown = ContactTuning.ClimbCooldownS;
                        Ball.ClimbAlongSpeed = 0f;
                    }
                }
            }

            bool solved = Ball.Step(World, direction, driveSpeed, Dt);
            Steps++;
            if (Ball.Center.y > HighestCenterY) HighestCenterY = Ball.Center.y;
            return solved;
        }
    }

    /// <summary>
    /// The terrain half of the v2 contact model (TERRAIN_CLIMB 6): terrain triangles are a
    /// contact SOURCE, the climb finishes on a ledge, and the cap still ends a wall that is
    /// taller than H_cap. Real triangles, the same solver the motor calls.
    /// </summary>
    public class TerrainClimbTests
    {
        private const float Radius = 0.111f;
        private const float Dt = 0.02f;

        private static ContactBall Ball(Vector3 center)
        {
            ContactBall ball = new ContactBall();
            ball.Center = center;
            ball.Radius = Radius;
            ball.ClimbSurfaceId = -1;
            return ball;
        }

        private static ContactWorld Empty()
        {
            ContactWorld world = new ContactWorld();
            world.HasGround = false;
            return world;
        }

        private static float Bottom(ContactBall ball)
        {
            return ball.Center.y - ball.Radius;
        }

        [Test]
        public void AFlatFloorSupportsThroughASkinContact()
        {
            TerrainFixture mesh = TerrainFixture.Floor();
            ContactBall ball = Ball(new Vector3(0f, Radius, 1f));
            mesh.Bind(ball);
            Assert.IsTrue(ball.Step(Empty(), new Vector3(0f, 0f, 1f), 0f, Dt));
            Assert.GreaterOrEqual(ball.DominantSurfaceId, TerrainFixture.FirstSurfaceId,
                "the terrain must be the support, not a hidden arena plane");
            Assert.GreaterOrEqual(ball.ContactCount, 1);
            Assert.AreEqual(1, ball.ReportNormal.y, 1e-3f);
            Assert.AreEqual(Radius, ball.Center.y, 0.002f, "the skin contact keeps the ball on the floor");
        }

        [Test]
        public void ARiserIsAClimbContactAndTheTopIsARideWithATopHeight()
        {
            TerrainFixture mesh = TerrainFixture.Step(0.19f, true);
            Vector3[] normals = new Vector3[ContactTuning.ContactCapacity];
            Vector3[] points = new Vector3[ContactTuning.ContactCapacity];
            float[] depths = new float[ContactTuning.ContactCapacity];
            float[] angles = new float[ContactTuning.ContactCapacity];
            int[] surfaceIds = new int[ContactTuning.ContactCapacity];
            int[] roles = new int[ContactTuning.ContactCapacity];
            int[] features = new int[ContactTuning.ContactCapacity];
            int[] classes = new int[ContactTuning.ContactCapacity];
            float[] topY = new float[ContactTuning.ContactCapacity];

            // 1 mm OUTSIDE the riser face: within the skin, so it registers with a NEGATIVE
            // depth (a skin contact is "near", never a push-out).
            int count;
            Assert.IsTrue(TerrainMeshMath.TryAppendContacts(
                new Vector3(0f, 0.10f, 2f - Radius - 0.001f), Radius,
                ContactTuning.ContactSkinM, mesh.Candidates, mesh.CandidateCount, mesh.MinX,
                mesh.MaxX, mesh.MinZ, mesh.MaxZ, mesh.SurfaceIds, mesh.Vertices,
                mesh.Indices, mesh.TriangleCount, null, null, null, null, null, 0,
                normals, points, depths, angles, surfaceIds, roles, features, classes,
                topY, ContactTuning.ContactCapacity, 0, out count));
            Assert.GreaterOrEqual(count, 1);
            int riser = -1;
            for (int i = 0; i < count; i++)
            {
                if (classes[i] == ContactCodes.ClassClimb && Mathf.Abs(normals[i].y) < 1e-3f)
                    riser = i;
            }
            Assert.GreaterOrEqual(riser, 0, "the vertical face must be a CLIMB contact");
            Assert.AreEqual(0.19f, topY[riser], 1e-4f, "the riser's top is the ledge");
            Assert.LessOrEqual(depths[riser], 0f, "a skin contact has depth <= 0");

            // Resting on the top face: RIDE, and the same ledge height.
            Assert.IsTrue(TerrainMeshMath.TryAppendContacts(
                new Vector3(0f, 0.19f + Radius, 3f), Radius, ContactTuning.ContactSkinM,
                mesh.Candidates, mesh.CandidateCount, mesh.MinX, mesh.MaxX, mesh.MinZ,
                mesh.MaxZ, mesh.SurfaceIds, mesh.Vertices, mesh.Indices, mesh.TriangleCount,
                null, null, null, null, null, 0, normals, points, depths, angles, surfaceIds,
                roles, features, classes, topY, ContactTuning.ContactCapacity, 0, out count));
            Assert.GreaterOrEqual(count, 1);
            bool rideOnTop = false;
            for (int i = 0; i < count; i++)
            {
                if (classes[i] == ContactCodes.ClassRide && Mathf.Abs(normals[i].y - 1f) < 1e-3f)
                    rideOnTop = true;
            }
            Assert.IsTrue(rideOnTop);
        }

        [Test]
        public void TheCapacityRefusesInsteadOfDroppingGeometry()
        {
            TerrainFixture mesh = TerrainFixture.Floor();
            Vector3[] normals = new Vector3[ContactTuning.ContactCapacity];
            Vector3[] points = new Vector3[ContactTuning.ContactCapacity];
            float[] depths = new float[ContactTuning.ContactCapacity];
            float[] angles = new float[ContactTuning.ContactCapacity];
            int[] surfaceIds = new int[ContactTuning.ContactCapacity];
            int[] roles = new int[ContactTuning.ContactCapacity];
            int[] features = new int[ContactTuning.ContactCapacity];
            int[] classes = new int[ContactTuning.ContactCapacity];
            float[] topY = new float[ContactTuning.ContactCapacity];
            int count;
            // The floor quad's two triangles share the diagonal (-2,0,0)-(2,0,4), which is
            // x = -1 at z = 1: a ball there touches BOTH, so one slot must refuse.
            Assert.IsFalse(TerrainMeshMath.TryAppendContacts(
                new Vector3(-1f, Radius, 1f), Radius, ContactTuning.ContactSkinM,
                mesh.Candidates, mesh.CandidateCount, mesh.MinX, mesh.MaxX, mesh.MinZ,
                mesh.MaxZ, mesh.SurfaceIds, mesh.Vertices, mesh.Indices, mesh.TriangleCount,
                null, null, null, null, null, 0, normals, points, depths, angles, surfaceIds,
                roles, features, classes, topY, 1, 0, out count),
                "two overlapping triangles into one slot must fail closed, not drop one");
        }

        [Test]
        public void TheBallClimbsOutOfA019MetrePitInAboutTwoSeconds()
        {
            TerrainFixture mesh = TerrainFixture.Step(0.19f, true);
            ContactBall ball = Ball(new Vector3(0f, Radius, 1f));
            mesh.Bind(ball);
            bool climbed = false;
            float maxStepRise = 0f;
            float previousBottom = Bottom(ball);
            for (int step = 0; step < 150; step++)
            {
                Assert.IsTrue(ball.Step(Empty(), new Vector3(0f, 0f, 1f), 4f, Dt, 20f));
                if (ball.ClimbActive) climbed = true;
                float rise = Bottom(ball) - previousBottom;
                if (rise > maxStepRise) maxStepRise = rise;
                previousBottom = Bottom(ball);
                if (Bottom(ball) >= 0.19f - 0.01f && ball.ReportNormal.y > 0.9f) break;
            }
            Assert.IsTrue(climbed, "the climb channel must have run against the riser");
            Assert.GreaterOrEqual(Bottom(ball), 0.19f - 0.01f,
                "the ball's bottom must reach the ledge");
            Assert.LessOrEqual(maxStepRise, 0.06f,
                "no single step may pop the ball up by a radius");
        }

        [Test]
        public void TheLedgeExitSpeedIsTenPerSecondTimesRadiusNotTenOverRadius()
        {
            // v = 10 (1/s) * r (m) = m/s. For the reference ball that is 1.11 m/s, NOT
            // 10 / 0.111 = 90 m/s. The clamp is the drive speed, so a big ball never
            // exceeds what the player pushes.
            Assert.AreEqual(1.11f, ClimbMath.LedgeExitSpeed(0.111f, 4f), 1e-3f);
            Assert.AreEqual(3.0f, ClimbMath.LedgeExitSpeed(0.30f, 4f), 1e-3f);
            Assert.AreEqual(4.0f, ClimbMath.LedgeExitSpeed(1.10f, 4f), 1e-3f,
                "the 2.20 m ball is clamped to the drive speed");
            Assert.AreEqual(3.0f, ClimbMath.LedgeExitSpeed(1.10f, 3f), 1e-3f);
            Assert.AreEqual(0.5f, ClimbMath.LedgeExitSpeed(0.01f, 4f), 1e-3f,
                "the floor keeps the exit moving");
            // One 50 Hz step of the exit's own speed stays below 80 mm (and below r), so the
            // exit cannot tunnel through the ledge it is crossing.
            float[] radii = { 0.111f, 0.30f, 0.55f, 1.10f };
            for (int i = 0; i < radii.Length; i++)
            {
                float speed = ClimbMath.LedgeExitSpeed(radii[i], 4f);
                Assert.LessOrEqual(speed * Dt, 0.08f, "step displacement at r=" + radii[i]);
                Assert.LessOrEqual(speed * Dt, radii[i], "and it stays inside one radius");
            }
        }

        [Test]
        public void AStackedRiserStillEndsOnTheTrueLedge()
        {
            // The same 0.19 m wall split into two stacked quads: the lower quad's top is a
            // FALSE low ledge (0.10). The exit may fire there, but the climb restarts on the
            // upper quad, so the END state must still be the true 0.19 ledge and no step may
            // pop. This is the triangulation sensitivity of contactTopY.
            TerrainFixture mesh = TerrainFixture.SplitRiser(0.19f, 0.10f);
            ContactBall ball = Ball(new Vector3(0f, Radius, 1f));
            mesh.Bind(ball);
            float maxStepRise = 0f;
            float previousBottom = Bottom(ball);
            for (int step = 0; step < 200; step++)
            {
                Assert.IsTrue(ball.Step(Empty(), new Vector3(0f, 0f, 1f), 4f, Dt, 20f));
                float rise = Bottom(ball) - previousBottom;
                if (rise > maxStepRise) maxStepRise = rise;
                previousBottom = Bottom(ball);
                if (Bottom(ball) >= 0.18f && ball.ReportNormal.y > 0.9f) break;
            }
            Assert.GreaterOrEqual(Bottom(ball), 0.19f - 0.01f,
                "the false low ledge must not stop the ball below the true top");
            Assert.LessOrEqual(maxStepRise, 0.06f);
        }

        [Test]
        public void AChamferedTopEndsAtTheTrueHeightWithoutAPop()
        {
            // A 45 deg bevel 0.05 m tall under the top: the riser's contactTopY is the true
            // ledge minus the bevel, so the exit fires early and the bevel's push-out rolls
            // the ball the rest of the way. The end height must be the top, no pop.
            TerrainFixture mesh = TerrainFixture.ChamferedStep(0.19f, 0.05f);
            ContactBall ball = Ball(new Vector3(0f, Radius, 1f));
            mesh.Bind(ball);
            float maxStepRise = 0f;
            float previousBottom = Bottom(ball);
            for (int step = 0; step < 200; step++)
            {
                Assert.IsTrue(ball.Step(Empty(), new Vector3(0f, 0f, 1f), 4f, Dt, 20f));
                float rise = Bottom(ball) - previousBottom;
                if (rise > maxStepRise) maxStepRise = rise;
                previousBottom = Bottom(ball);
                if (Bottom(ball) >= 0.18f && ball.ReportNormal.y > 0.9f) break;
            }
            Assert.GreaterOrEqual(Bottom(ball), 0.19f - 0.01f, "it must end on the top");
            Assert.LessOrEqual(maxStepRise, 0.06f);
        }

        [Test]
        public void AWallWhoseTopIsFarAboveTheBallNeverHovers()
        {
            // RefCancer's spawn pit: the building wall in front of the ball is ONE strip whose
            // top is 13.85 m, so contactTopY = 13.85 is that wall's TRUE top and the ledge exit
            // can never fire. The cap must govern, and the ball must never be lifted above it
            // (the R25b failure mode was a hang that kept climbing with no ledge to reach).
            TerrainFixture mesh = TerrainFixture.Step(13.85f, false);
            ContactBall ball = Ball(new Vector3(0f, Radius, 1f));
            mesh.Bind(ball);
            float cap = ClimbMath.ClimbHeightCap(Radius, 0f);
            float maxBottom = 0f;
            for (int step = 0; step < 250; step++)
            {
                Assert.IsTrue(ball.Step(Empty(), new Vector3(0f, 0f, 1f), 4f, Dt, 20f));
                if (Bottom(ball) > maxBottom) maxBottom = Bottom(ball);
            }
            Assert.LessOrEqual(maxBottom, cap + 0.03f,
                "a wall with no reachable ledge must stop at H_cap, not hover above it");
            Assert.GreaterOrEqual(maxBottom, 0.10f, "it still climbs before the cap");
        }

        [Test]
        public void AWallTallerThanTheCapStopsAtTheCapAndNeverPops()
        {
            TerrainFixture mesh = TerrainFixture.Step(2.0f, false);
            ContactBall ball = Ball(new Vector3(0f, Radius, 1f));
            mesh.Bind(ball);
            float cap = ClimbMath.ClimbHeightCap(Radius, 0f);
            float maxBottom = 0f;
            for (int step = 0; step < 200; step++)
            {
                Assert.IsTrue(ball.Step(Empty(), new Vector3(0f, 0f, 1f), 4f, Dt, 20f));
                if (Bottom(ball) > maxBottom) maxBottom = Bottom(ball);
            }
            Assert.GreaterOrEqual(maxBottom, 0.10f, "it does climb before the cap");
            Assert.LessOrEqual(maxBottom, cap + 0.03f,
                "H_cap must bound the climb even when the wall keeps going");
        }
        // ---- The cap is a budget of the wall-hugging stretch, not of one face ----
        // (TERRAIN_CLIMB 6.6: the narrow-V ratchet, 0.69 m on a 0.60 m cap in the field.)

        private const float VRadius = 0.15f;

        /// <summary>
        /// Two 3 m tall vertical faces whose normals are <paramref name="halfAngleDeg"/>
        /// either side of -Z (the inner angle is 180 - 2 x that), apex at z = 3. The same
        /// geometry as ContactModelV2Tests' narrow wedge.
        /// </summary>
        private static ContactWorld NarrowV(float halfAngleDeg)
        {
            float angle = halfAngleDeg * Mathf.Deg2Rad;
            Vector3 normalA = new Vector3(Mathf.Sin(angle), 0f, -Mathf.Cos(angle));
            Vector3 normalB = new Vector3(-Mathf.Sin(angle), 0f, -Mathf.Cos(angle));
            Vector3 apex = new Vector3(0f, 1.5f, 3f);
            ContactWorld world = new ContactWorld();
            AddFace(world, apex - normalA * 0.5f, normalA, 2);
            AddFace(world, apex - normalB * 0.5f, normalB, 3);
            return world;
        }

        private static void AddFace(ContactWorld world, Vector3 center, Vector3 normal,
            int surfaceId)
        {
            Vector3 axisY = new Vector3(0f, 1f, 0f);
            Vector3 axisX = Vector3.Cross(axisY, normal).normalized;
            world.Add(center, axisX, axisY, normal, new Vector3(3f, 1.5f, 0.5f), surfaceId,
                ContactCodes.RoleBox);
        }

        [TestCase(55f)]
        [TestCase(65f)]
        [TestCase(72f)]
        public void AHeldPushIntoANarrowVNeverClimbsPastTheCap(float halfAngleDeg)
        {
            // 40 s of full push into a V (65 deg: the 50 deg wedge), over the facings and
            // lateral offsets that make the velocity swing between the faces. Rest + H_cap is
            // the budget of the whole stretch; before the fix the face the climb started on
            // changed, each change re-anchored at the current height, and the peak ratcheted
            // to 0.69-1.0 m. The 5 mm is the contact skin.
            float cap = ClimbMath.ClimbHeightCap(VRadius, 0f);
            float[] headings = { 0f, 2f, 5f, 8f, 12f, 18f, 30f };
            float[] offsets = { 0f, 0.07f };
            float highest = 0f;
            for (int o = 0; o < offsets.Length; o++)
            {
                for (int h = 0; h < headings.Length; h++)
                {
                    ClimbMotorLoop loop = new ClimbMotorLoop(NarrowV(halfAngleDeg),
                        new Vector3(offsets[o], VRadius, 2.6f), VRadius, headings[h]);
                    bool everClimbed = false;
                    for (int step = 0; step < 2000; step++)
                    {
                        Assert.IsTrue(loop.Step());
                        everClimbed = everClimbed || loop.Ball.ClimbActive;
                    }
                    Assert.LessOrEqual(loop.HighestCenterY, VRadius + cap + 0.005f,
                        "half angle " + halfAngleDeg + " heading " + headings[h] + " offset "
                        + offsets[o] + ": the centre peaked at " + loop.HighestCenterY
                        + " against rest + H_cap = " + (VRadius + cap));
                    Assert.IsTrue(everClimbed, "the push must open the climb, heading "
                        + headings[h]);
                    if (loop.HighestCenterY > highest) highest = loop.HighestCenterY;
                }
            }
            Assert.GreaterOrEqual(highest, VRadius + 0.5f * cap,
                "the cap must be REACHED somewhere: a bound nothing approaches proves nothing");
        }

        [Test]
        public void AClimbThatStartsOnAnotherFaceKeepsTheAnchorOfTheStretch()
        {
            // The ball hangs at y = 0.40 against a wall, not standing, and remembers a
            // DIFFERENT face (normal +X, another surface) from a climb whose base was y = 0.15.
            // The new climb is the same wall-hugging stretch: the anchor stays 0.15 (a face
            // change used to re-anchor at 0.40, which is how the cap ratcheted).
            ContactWorld world = ContactScenes.WallWorld();
            ContactBall ball = new ContactBall();
            ball.Radius = VRadius;
            ball.Center = new Vector3(0f, 0.40f, 0.15f);
            ball.PlanarVelocity = new Vector3(0f, 0f, -4f);
            ball.ClimbSurfaceId = 7;
            ball.ClimbAnchorY = 0.15f;
            ball.ClimbNormal = new Vector3(1f, 0f, 0f);
            ball.ClimbPoint = new Vector3(0.5f, 0.2f, 0f);
            ball.ClimbTopY = float.MaxValue;
            Assert.IsTrue(ball.Step(world, new Vector3(0f, 0f, -1f), 4f, Dt));
            Assert.IsTrue(ball.ClimbActive, "the push into the wall must open a climb");
            Assert.AreEqual(0f, ball.Center.z - VRadius - 0f, 0.05f,
                "the ball is against the wall's face (z = 0)");
            Assert.AreEqual(0.15f, ball.ClimbAnchorY, 1e-5f,
                "the cap budget belongs to the stretch, not to a face");

            // A start LOWER than the old anchor (it fell below its base, still on a wall)
            // lowers the anchor: the cap is measured from the lowest point.
            ContactBall lower = new ContactBall();
            lower.Radius = VRadius;
            lower.Center = new Vector3(0f, 0.40f, 0.15f);
            lower.PlanarVelocity = new Vector3(0f, 0f, -4f);
            lower.ClimbSurfaceId = 7;
            lower.ClimbAnchorY = 0.90f;
            lower.ClimbNormal = new Vector3(1f, 0f, 0f);
            lower.ClimbPoint = new Vector3(0.5f, 0.9f, 0f);
            lower.ClimbTopY = float.MaxValue;
            Assert.IsTrue(lower.Step(world, new Vector3(0f, 0f, -1f), 4f, Dt));
            Assert.IsTrue(lower.ClimbActive);
            Assert.AreEqual(0.40f, lower.ClimbAnchorY, 0.01f,
                "an anchor above the ball is replaced by the lower start");
        }

        [Test]
        public void StandingOnSomethingMovesTheAnchorToWhereTheBallStands()
        {
            // The anchor follows where the ball STANDS. A ball resting on a 0.30 m deck whose
            // anchor is still the floor's 0.15 (the climb that put it there ended on the
            // ledge) gets the deck as its base, so the next wall it meets has its own budget.
            ContactWorld world = new ContactWorld();
            world.HasGround = true;
            world.Add(new Vector3(0f, 0.15f, 5f), new Vector3(1f, 0f, 0f),
                new Vector3(0f, 1f, 0f), new Vector3(0f, 0f, 1f),
                new Vector3(3f, 0.15f, 3f), 5, ContactCodes.RoleBox);
            ContactBall ball = new ContactBall();
            ball.Radius = VRadius;
            ball.Center = new Vector3(0f, 0.30f + VRadius, 5f);
            ball.ClimbSurfaceId = 3;
            ball.ClimbAnchorY = 0.15f;
            Assert.IsTrue(ball.Step(world, new Vector3(0f, 0f, 1f), 0f, Dt));
            Assert.AreEqual(ContactCodes.ClassRide, ball.DominantClass);
            Assert.AreEqual(0.30f + VRadius, ball.ClimbAnchorY, 0.01f);
        }

        [Test]
        public void ARememberedClimbFaceIsKeptWhileThePushStillHoldsIt()
        {
            // A 65 deg V. The climb opens on face A (the drive points at A's side), then the
            // velocity direction swings to the other side: B is now the face the drive
            // "pushes into most", but A's push-in is still 0.26 (>= p_min), so the solver
            // keeps reading A. Without the hysteresis the remembered face flips to B.
            ContactWorld world = NarrowV(65f);
            ContactBall ball = new ContactBall();
            ball.Radius = VRadius;
            ball.Center = new Vector3(0f, VRadius, 2.6f);
            ball.PlanarVelocity = new Vector3(0f, 0f, 3f);
            ball.ClimbSurfaceId = -1;
            float toA = -10f * Mathf.Deg2Rad;
            Vector3 driveToA = new Vector3(Mathf.Sin(toA), 0f, Mathf.Cos(toA));
            for (int i = 0; i < 40 && !ball.ClimbActive; i++)
            {
                Assert.IsTrue(ball.Step(world, driveToA, 4f, Dt));
            }
            Assert.IsTrue(ball.ClimbActive, "the climb must open in the V");
            Assert.Greater(ball.ClimbNormal.x, 0.5f, "the drive's side picked face A (+X normal)");

            float toB = 10f * Mathf.Deg2Rad;
            Vector3 driveToB = new Vector3(Mathf.Sin(toB), 0f, Mathf.Cos(toB));
            for (int i = 0; i < 25; i++)
            {
                Assert.IsTrue(ball.Step(world, driveToB, 4f, Dt));
                Assert.IsTrue(ball.ClimbActive, "the push still holds face A, step " + i);
                Assert.Greater(ball.ClimbNormal.x, 0.5f,
                    "the remembered face stays A while its push-in holds, step " + i);
            }

            // Turn all the way to B's side (A's push-in < p_min): the face hands over to B in
            // the SAME step, with no release in between.
            float farB = 40f * Mathf.Deg2Rad;
            Vector3 driveFarB = new Vector3(Mathf.Sin(farB), 0f, Mathf.Cos(farB));
            Assert.IsTrue(ball.Step(world, driveFarB, 4f, Dt));
            Assert.IsTrue(ball.ClimbActive, "a hand-over to the other face is not a release");
            Assert.Less(ball.ClimbNormal.x, -0.5f, "and the climb now reads face B");
        }

        [Test]
        public void TheClimbAnchorIsTheLowestCentreOfTheStretch()
        {
            // ClimbMath.ClimbAnchorAtStart: unknown anchor -> the centre; a known anchor
            // stands for any face and only a LOWER start moves it down.
            Assert.AreEqual(0.40f, ClimbMath.ClimbAnchorAtStart(false, 9f, 0.40f), 1e-6f);
            Assert.AreEqual(0.15f, ClimbMath.ClimbAnchorAtStart(true, 0.15f, 0.40f), 1e-6f);
            Assert.AreEqual(0.40f, ClimbMath.ClimbAnchorAtStart(true, 0.90f, 0.40f), 1e-6f);
            Assert.AreEqual(0.40f, ClimbMath.ClimbAnchorAtStart(true, 0.40f, 0.40f), 1e-6f);
        }
    }
}
