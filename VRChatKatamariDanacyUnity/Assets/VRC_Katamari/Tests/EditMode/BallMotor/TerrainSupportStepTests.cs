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
            host = new GameObject("TerrainSupportStepTests");
            host.hideFlags = HideFlags.DontSave;
            motor = host.AddComponent(motorType);
            Invoke("Start");
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
