using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace VRCKatamari.Tests
{
    /// <summary>
    /// The motor-level half of the 2026-11-16 terrain support fix: the mesh really does take
    /// the support away from the arena plane through <c>ResolveTerrainSupport</c>, and the
    /// microsecond counter really measures the query.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Same headless pattern as <see cref="ContactModelV2Tests"/>: the motor is a
    /// <c>UdonSharpBehaviour</c>, so in EditMode it is added to a temporary
    /// <see cref="GameObject"/> through reflection and stepped with an explicit
    /// <c>StepBall(0.02f)</c> (that method takes its delta as an argument and reads no
    /// <c>Time.deltaTime</c>). The ball's serialized start is the 0.2997 m ball at
    /// (0, 0.15, 0), so its bottom starts 0.15 mm above y=0 and the step limit is 0.0749 m.
    /// </para>
    /// <para>
    /// The host object is marked <see cref="HideFlags.DontSave"/> and destroyed in
    /// <see cref="TearDown"/>, so a test run leaves nothing in the open scene and nothing in
    /// a saved one.
    /// </para>
    /// </remarks>
    public class TerrainSupportStepTests
    {
        private const string MotorTypeName =
            "VRCKatamari.BallMotor.KatamariBallMotor, VRCKatamari.Runtime";
        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic |
            BindingFlags.Public;

        private GameObject host;
        private Component motor;
        private Type motorType;

        [SetUp]
        public void SetUp()
        {
            motorType = Type.GetType(MotorTypeName);
            if (motorType == null)
            {
                foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    Type candidate = assembly.GetType("VRCKatamari.BallMotor.KatamariBallMotor");
                    if (candidate != null)
                    {
                        motorType = candidate;
                        break;
                    }
                }
            }
            Assert.IsNotNull(motorType, "KatamariBallMotor type not loaded");
            CreateMotor();
        }

        /// <summary>
        /// A fresh motor on a fresh host. A case that needs a different mesh (one terrain per
        /// motor) recreates instead of re-entering SetUp/TearDown, so nothing leaks.
        /// </summary>
        private void CreateMotor()
        {
            host = new GameObject("TerrainSupportStepTests");
            host.hideFlags = HideFlags.DontSave;
            motor = host.AddComponent(motorType);
            Invoke("Start");
        }

        private void RecreateMotor()
        {
            if (host != null) UnityEngine.Object.DestroyImmediate(host);
            host = null;
            motor = null;
            CreateMotor();
        }

        private void SetField(string name, object value)
        {
            FieldInfo field = motorType.GetField(name, Any);
            Assert.IsNotNull(field, name + " field not found");
            field.SetValue(motor, value);
        }

        private object GetField(string name)
        {
            FieldInfo field = motorType.GetField(name, Any);
            Assert.IsNotNull(field, name + " field not found");
            return field.GetValue(motor);
        }

        private void RegisterMesh(Vector3[] vertices, int[] indices, int[] starts, int[] counts,
            int[] zones, int chunkCount)
        {
            Invoke("SetTerrainMeshContactEnabled", true);
            Assert.IsTrue((bool)Invoke("SetTerrainMesh", vertices, vertices.Length, indices,
                indices.Length, starts, counts, zones, chunkCount), "the mesh registered");
        }

        [TearDown]
        public void TearDown()
        {
            if (host != null) UnityEngine.Object.DestroyImmediate(host);
            host = null;
            motor = null;
        }

        private object Invoke(string name, params object[] args)
        {
            MethodInfo method = motorType.GetMethod(name, Any);
            Assert.IsNotNull(method, name + " not found");
            return method.Invoke(motor, args);
        }

        private void Step(int count)
        {
            for (int i = 0; i < count; i++)
            {
                Invoke("StepBall", 0.02f);
            }
        }

        private static Vector3[] GridVertices(int cells)
        {
            // (cells + 1)^2 vertices over a 4 x 4 m patch; two triangles per cell.
            int side = cells + 1;
            Vector3[] vertices = new Vector3[side * side];
            for (int z = 0; z < side; z++)
            {
                for (int x = 0; x < side; x++)
                {
                    vertices[z * side + x] = new Vector3(-2f + 4f * x / cells, 0f,
                        -2f + 4f * z / cells);
                }
            }
            return vertices;
        }

        private static int[] GridIndices(int cells)
        {
            int side = cells + 1;
            int[] indices = new int[cells * cells * 6];
            int at = 0;
            for (int z = 0; z < cells; z++)
            {
                for (int x = 0; x < cells; x++)
                {
                    int corner = z * side + x;
                    indices[at] = corner;
                    indices[at + 1] = corner + side;
                    indices[at + 2] = corner + side + 1;
                    indices[at + 3] = corner;
                    indices[at + 4] = corner + side + 1;
                    indices[at + 5] = corner + 1;
                    at = at + 6;
                }
            }
            return indices;
        }

        private void RegisterFlatMesh(float height, int cells)
        {
            Vector3[] vertices = GridVertices(cells);
            for (int i = 0; i < vertices.Length; i++)
            {
                vertices[i] = new Vector3(vertices[i].x, height, vertices[i].z);
            }
            int[] indices = GridIndices(cells);
            int[] starts = { 0 };
            int[] counts = { indices.Length / 3 };
            int[] zones = { 0 };
            Invoke("SetTerrainMeshContactEnabled", true);
            Assert.IsTrue((bool)Invoke("SetTerrainMesh", vertices, vertices.Length, indices,
                indices.Length, starts, counts, zones, 1), "the mesh registered");
            Assert.AreEqual(indices.Length / 3, (int)Invoke("GetTerrainMeshTriangleCount"));
        }

        [Test]
        public void TheMeshTakesTheSupportAwayFromAPlaneThatSitsAboveIt()
        {
            // The 2026-11-16 C22 configuration, at the unit level: the copied arena plane is
            // at y=0 and the ball's floor (the mesh) is 1 m BELOW it. Before the fix the
            // plane won because the mesh could only be adopted by being HIGHER.
            Assert.IsTrue((bool)Invoke("SetGroundPlane", new Vector3(0f, 1f, 0f), 0f),
                "the arena plane registers");
            RegisterFlatMesh(-1f, 16);

            Step(1);

            Assert.AreEqual(6, (int)Invoke("GetSupportKind"),
                "the selected support is a terrain triangle, not the ground plane");
            Assert.AreEqual(2000, (int)Invoke("GetSupportSurfaceId"),
                "first terrain surface id (0 zone)");
            Assert.That((float)Invoke("GetSupportSurfaceHeightWorldM"),
                Is.EqualTo(-1f).Within(1e-3f),
                "the ball's support is the mesh 1 m down, so it starts to fall into it");
        }

        [Test]
        public void TheSupportAndPenetrationCostsAreSeparateAndTheWorkCountersExplainThem()
        {
            // The support ring alone: the ball is 0.15 m above the mesh, so the sphere's AABB
            // does not overlap any triangle and T1 has nothing to do.
            RegisterFlatMesh(0f, 16);
            Step(1);

            float support = (float)Invoke("GetLastTerrainSupportMicros");
            float penetration = (float)Invoke("GetLastTerrainPenetrationMicros");
            float total = (float)Invoke("GetLastTerrainQueryMicros");
            int tested = (int)Invoke("GetTerrainTrianglesTested");
            int fullEvals = (int)Invoke("GetTerrainFullEvals");

            Assert.That(support, Is.GreaterThan(0f), "the support ring is the timed half");
            Assert.That(penetration, Is.GreaterThanOrEqualTo(0f));
            Assert.That(total, Is.EqualTo(support + penetration).Within(1e-3f),
                "the total is exactly the two halves, so a reader can attribute the cost");
            Assert.That(support, Is.GreaterThan(penetration),
                "the 17-probe ring dominates an empty push-out scan");

            // Explainable work: all 17 probes walk the SAME candidate list, so
            // trianglesTested == 17 x candidates is EXACT (it was 17 x 512 before the grid,
            // when every probe walked the whole chunk).
            int candidates = (int)Invoke("GetTerrainCandidates");
            Assert.That(candidates, Is.GreaterThan(0), "the grid offered a candidate list");
            Assert.AreEqual(17 * candidates, tested,
                "17 probes x the support candidate list, no per-probe re-scan");
            Assert.That(tested, Is.LessThan(17 * 512),
                "and that is strictly less than the pre-grid full scan");
            Assert.That(fullEvals, Is.GreaterThan(0), "at least the triangle under the ball");
            Assert.That(fullEvals, Is.LessThan(tested),
                "the per-triangle AABB rejects some of what the window kept");

            // A penetration case: drop the ball's centre to 0.10 m so its sphere is 0.05 m
            // INTO the floor from the front side (T1 is single-sided; a ball under a +Y face
            // is never pushed, which is the roof rule). The scan now visits the whole chunk
            // and the closest-point branch runs, so the penetration half has real work.
            RecreateMotor();
            RegisterFlatMesh(0f, 16);
            SetField("ballCenterWorld", new Vector3(0f, 0.10f, 0f));
            Step(1);
            float buriedPenetration = (float)Invoke("GetLastTerrainPenetrationMicros");
            Assert.That(buriedPenetration, Is.GreaterThan(0f),
                "a sphere 0.05 m inside the floor pays for the T1 scan");
            Assert.That(buriedPenetration, Is.GreaterThan(penetration),
                "and strictly more than the empty scan above");
            // The two candidate counts are separate readings, not one overwritten number:
            // T1 collected at its own centre (which the push then moved), the support query
            // recollected after it.
            Assert.That((int)Invoke("GetTerrainPenetrationCandidates"), Is.GreaterThan(0),
                "the penetration count is its own reading");
            Assert.That((int)Invoke("GetTerrainCandidates"), Is.GreaterThan(0),
                "and the support count is too, read from its own field");
            Assert.That((float)Invoke("GetLastTerrainQueryMicros"),
                Is.EqualTo((float)Invoke("GetLastTerrainSupportMicros") +
                    (float)Invoke("GetLastTerrainPenetrationMicros")).Within(1e-3f));
            Assert.That(GetCenterY(), Is.GreaterThan(0.14f),
                "T1 pushed it back out and the ground solve put it on the surface");
        }

        private float GetCenterY()
        {
            Vector3 center = (Vector3)Invoke("GetCenterWorld");
            return center.y;
        }

        [Test]
        public void TheV2AuthorityDoesNotLeaveAStaleTerrainReading()
        {
            // The bug this pins: the counters were reset inside StepPositionV1, so a step that
            // did not enter that method (the v2 authority) kept the previous v1 reading. Reset
            // now lives in StepBall, which BOTH authorities pass through. The v2 step here has
            // the terrain contact switched OFF, so "no terrain query happened" is guaranteed
            // and the assertion is about the RESET, not about what v2 happens to query.
            RegisterFlatMesh(0f, 16);
            Step(1);
            Assert.That((float)Invoke("GetLastTerrainQueryMicros"), Is.GreaterThan(1f),
                "the v1 step measured something");
            Assert.That((int)Invoke("GetTerrainTrianglesTested"), Is.GreaterThan(0));

            Invoke("SetTerrainMeshContactEnabled", false);
            Invoke("SetContactModelV2Enabled", true);
            Assert.IsTrue((bool)Invoke("IsContactModelV2"));
            Step(1);

            Assert.AreEqual(0f, (float)Invoke("GetLastTerrainQueryMicros"), 1e-6f,
                "the v2 authority does not query the mesh, so the reading must be ZERO, " +
                "not the previous v1 step's value");
            Assert.AreEqual(0f, (float)Invoke("GetLastTerrainSupportMicros"), 1e-6f);
            Assert.AreEqual(0f, (float)Invoke("GetLastTerrainPenetrationMicros"), 1e-6f);
            Assert.AreEqual(0, (int)Invoke("GetTerrainTrianglesTested"));
            Assert.AreEqual(0, (int)Invoke("GetTerrainFullEvals"));
        }

        [Test]
        public void TheGridCutsTheTrianglesTheSupportQueryWalks()
        {
            // 4 x 4 m of 16 x 16 quads = 512 triangles in ONE chunk: before the grid every
            // probe walked all 512. The grid cuts the ball's footprint down to the 1 m cell it
            // stands in, and the invariants below say exactly how much work that saved.
            RegisterFlatMesh(0f, 16);
            Step(1);
            Assert.IsTrue((bool)Invoke("IsTerrainGridReady"), "a 4 x 4 m mesh is grid-able");
            int cells = (int)Invoke("GetTerrainGridCellCount");
            int candidates = (int)Invoke("GetTerrainCandidates");
            int tested = (int)Invoke("GetTerrainTrianglesTested");
            Assert.That(cells, Is.GreaterThan(0));
            Assert.That((float)Invoke("GetTerrainGridCellM"),
                Is.EqualTo(1f).Within(1e-6f), "the default cell size");
            Assert.That(candidates, Is.GreaterThan(0));
            Assert.That(candidates, Is.LessThan(512 / 4),
                "one 1 m cell of a 16 x 16 grid holds far fewer than the whole mesh");
            Assert.AreEqual(17 * candidates, tested,
                "17 probes walk exactly the candidate list; the full scan would walk 17 x 512");
            Assert.That(tested, Is.LessThan(17 * 512 / 2),
                "the grid at least halves the per-step triangle iterations on this fixture");
        }

        [Test]
        public void TheV2SolverReadsTheContactListItCollectedLast()
        {
            // THE ORDER CONTRACT. Both the v2 support query and the v2 contact collection
            // write the SAME terrainCandidates array; the solver reads (array, count) as one
            // pair. If the support collection runs last, the solver reads the support list
            // with the sweep count: candidates lost where the two lists differ, duplicates
            // where the old tail survives.
            //
            // The lip makes that observable: a thin quad toward -Z at z in [-0.1545,
            // -0.1535] is OUTSIDE the support window (r = 0.14985, plus the 1e-6 slack) but
            // INSIDE the contact window (r + half the step travel + the 5 mm contact skin =
            // 0.15485). It is registered FIRST, so it sits at the front of the candidate list
            // - exactly the part a later support collection would overwrite.
            Vector3[] vertices =
            {
                new Vector3(-0.2f, 0f, -0.1545f), new Vector3(-0.2f, 0f, -0.1535f),
                new Vector3(0.2f, 0f, -0.1535f), new Vector3(0.2f, 0f, -0.1545f),
                new Vector3(-2f, 0f, -2f), new Vector3(-2f, 0f, 2f),
                new Vector3(2f, 0f, 2f), new Vector3(2f, 0f, -2f),
            };
            int[] indices = { 0, 1, 2, 0, 2, 3, 4, 5, 6, 4, 6, 7 };
            RegisterMesh(vertices, indices, new[] { 0 }, new[] { 4 }, new[] { 0 }, 1);
            Assert.IsTrue((bool)Invoke("IsTerrainGridReady"), "the 4-triangle mesh is grid-able");
            Invoke("SetContactModelV2Enabled", true);
            Assert.IsTrue((bool)Invoke("IsContactModelV2"));

            // Drive toward -X and -Z (heading 0 faces +Z; forward -1 and lateral -1 give the
            // negative quadrant).
            SetField("debugForward", -1f);
            SetField("debugLateral", -1f);
            Vector3 before = (Vector3)Invoke("GetCenterWorld");
            Step(1);

            int contactCandidates = (int)Invoke("GetTerrainContactCandidates");
            int supportCandidates = (int)Invoke("GetTerrainCandidates");
            Assert.That(supportCandidates, Is.GreaterThan(0), "the support query ran first");
            Assert.That(contactCandidates, Is.GreaterThan(supportCandidates),
                "the contact window (r + travel/2 + skin) holds the support set AND the lip");
            int[] array = (int[])GetField("terrainCandidates");
            Assert.IsNotNull(array);
            bool lipPresent = false;
            for (int i = 0; i < contactCandidates; i++)
            {
                if (array[i] == 0 || array[i] == 1) lipPresent = true;
            }
            Assert.IsTrue(lipPresent,
                "the contact list still holds the lip (triangles 0/1): the support collection " +
                "must not have overwritten the list the solver reads");
            for (int i = 0; i < contactCandidates; i++)
            {
                for (int j = i + 1; j < contactCandidates; j++)
                {
                    Assert.AreNotEqual(array[i], array[j],
                        "the contact list has no duplicate triangles");
                }
            }
            Assert.That((int)Invoke("GetContactCount"), Is.GreaterThan(0),
                "the solver received terrain contacts, not an empty list");

            // And the ball really travels toward -X/-Z on the mesh without falling through.
            Step(10);
            Vector3 after = (Vector3)Invoke("GetCenterWorld");
            Assert.That(after.x, Is.LessThan(before.x - 0.005f), "moved toward -X");
            Assert.That(after.z, Is.LessThan(before.z - 0.005f), "moved toward -Z");
            Assert.That(after.y, Is.GreaterThan(0.13f), "still standing on the terrain mesh");
        }

        [Test]
        public void TheTerrainQueryMicrosecondsAreReal()
        {
            // The counter used Time.time, a FRAME timestamp, so two reads inside one step
            // were identical and the four-cell sample read 0 on every step. 512 triangles
            // x 17 probes is far above any plausible timer resolution.
            RegisterFlatMesh(0f, 16);
            Step(1);
            float micros = (float)Invoke("GetLastTerrainQueryMicros");
            Assert.That(micros, Is.GreaterThan(1f),
                "a real query must measure more than 1 us; 0 means the clock did not advance");
            Assert.That(micros, Is.LessThan(100000f),
                "the reading is microseconds, not seconds or milliseconds");
        }
    }
}
