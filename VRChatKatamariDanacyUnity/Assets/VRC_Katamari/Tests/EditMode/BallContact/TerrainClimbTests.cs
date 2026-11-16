using NUnit.Framework;
using UnityEngine;
using VRCKatamari.BallContact;

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
    }
}
