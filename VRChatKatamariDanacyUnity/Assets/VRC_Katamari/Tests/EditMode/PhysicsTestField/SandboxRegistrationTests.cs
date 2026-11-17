using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace VRCKatamari.Tests
{
    /// <summary>
    /// Pins the physics test field's one registration: the manual sandbox (a frame host with the
    /// big floor and the outer walls, then every cell's boards appended; a cell carries no floor
    /// and no wall of its own) and the importer rule that lets a cell document omit them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The sandbox is <c>LevelArenaHost.Register(clearFirst, boxIdOffset, rampIdOffset)</c>;
    /// <c>Reapply</c> is <c>Register(true, 0, 0)</c> and stays what every other level uses. These
    /// tests drive that method on a real motor, so they pin what the sandbox driver sends it. The driver's own state machine needs a scene and is
    /// covered by the Unity round plus the source-shape checks in
    /// <c>Docs/tests/levelbuild/test_physics_test_field_cells.py</c>.
    /// </para>
    /// <para>
    /// Reflection only, for the reason <c>LevelArenaHostObbTests</c> gives: this assembly does not
    /// reference UdonSharp, and the Labs code is not in <c>VRCKatamari.Runtime</c>.
    /// </para>
    /// </remarks>
    public class SandboxRegistrationTests
    {
        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic |
            BindingFlags.Public;

        // The 13 wired cells (the criteria file's wired=true rows; the Python guard keeps this
        // list equal to them). C21-C24 are terrain design cells and are not in the sandbox.
        private static readonly string[] WiredCells =
        {
            "C01", "C03", "C05", "C06", "C08", "C09", "C10", "C13", "C14", "C25", "C26", "C27",
            "C28",
        };

        private const string LevelsRepoRelative = "Docs/Design/Levels/physics_test_field";

        private GameObject motorHost;
        private Component motor;
        private Type motorType;
        private Type arenaType;
        private readonly List<GameObject> created = new List<GameObject>();

        [SetUp]
        public void SetUp()
        {
            motorType = FindType("VRCKatamari.BallMotor.KatamariBallMotor");
            arenaType = FindType("VRCKatamari.Story.LevelArenaHost");
            Assert.IsNotNull(motorType, "KatamariBallMotor type not loaded");
            Assert.IsNotNull(arenaType, "LevelArenaHost type not loaded");
            motorHost = new GameObject("SandboxRegistrationTestsMotor");
            motor = motorHost.AddComponent(motorType);
            Invoke(motor, motorType, "Start");
        }

        [TearDown]
        public void TearDown()
        {
            for (int i = 0; i < created.Count; i++)
            {
                if (created[i] != null) UnityEngine.Object.DestroyImmediate(created[i]);
            }

            created.Clear();
            if (motorHost != null) UnityEngine.Object.DestroyImmediate(motorHost);
        }

        // ---- the sandbox registration ------------------------------------------

        [Test]
        public void SandboxRegistersEveryCellsBoardsUnderOneFrame()
        {
            Component frame = NewArena("frame", null, null, 4);
            Component cellA = NewArena("cellA", new[] { Box("a_deck", 0f) }, new[] { Ramp("a_ramp", 4f) }, 0);
            Component cellB = NewArena("cellB", new[] { Box("b_deck", 8f), Box("b_wall", 12f) }, null, 0);

            // The frame clears the motor and brings the big floor's 4 outer planes; each cell
            // then appends its boards (it has no wall of its own).
            Invoke(frame, arenaType, "Register", true, 0, 0);
            int boxes = 0;
            int ramps = 0;
            Component[] cells = { cellA, cellB };
            for (int i = 0; i < cells.Length; i++)
            {
                Invoke(cells[i], arenaType, "Register", false, boxes, ramps);
                boxes += (int)Invoke(cells[i], arenaType, "GetRegisteredBoxCount");
                ramps += (int)Invoke(cells[i], arenaType, "GetRegisteredRampCount");
            }

            Assert.AreEqual(4, GetIntField("planeCount"), "only the frame's 4 outer planes");
            Assert.AreEqual(4, (int)Invoke(frame, arenaType, "GetRegisteredPlaneCount"));
            Assert.AreEqual(0, (int)Invoke(cellA, arenaType, "GetRegisteredPlaneCount"));
            Assert.AreEqual(0, (int)Invoke(cellB, arenaType, "GetRegisteredPlaneCount"));
            StringAssert.StartsWith("[arena] planes=0/0 ", (string)Invoke(cellA, arenaType, "GetSetupReport"));

            Assert.AreEqual(4, (int)Invoke(motor, motorType, "GetStaticObbCount"),
                "every cell's solids are in the one table: 1 box + 1 ramp + 2 boxes");
            Assert.AreEqual(3, boxes);
            Assert.AreEqual(1, ramps);

            // The contact solver de-duplicates by surface id, so appended hosts must not reuse
            // an id: ramp slab 500, then boxes 1, 2, 3.
            CollectionAssert.AreEquivalent(new[] { 500, 1, 2, 3 }, StaticSurfaceIds(),
                "surface ids are unique across the appended cells");
            Assert.AreEqual(0, (int)Invoke(motor, motorType, "GetStaticObbOverflowCount"));
        }

        [Test]
        public void ReapplyIsUnchangedForALevelThatCarriesItsOwnWalls()
        {
            // Every other level: Reapply = clear + everything, its own walls included, ids from 0.
            Component level = NewArena("level", new[] { Box("deck", 0f) }, new[] { Ramp("ramp", 4f) }, 4);
            Component frame = NewArena("frame", null, null, 4);
            Invoke(frame, arenaType, "Register", true, 0, 0);

            Invoke(level, arenaType, "Reapply");

            Assert.AreEqual(4, GetIntField("planeCount"),
                "Reapply cleared the frame's planes and registered the level's own 4 walls");
            Assert.AreEqual(4, (int)Invoke(level, arenaType, "GetRegisteredPlaneCount"));
            Assert.AreEqual(2, (int)Invoke(motor, motorType, "GetStaticObbCount"));
            CollectionAssert.AreEquivalent(new[] { 500, 1 }, StaticSurfaceIds(),
                "ids start at box 1 and ramp 500, as before the sandbox existed");
            StringAssert.StartsWith("[arena] planes=4/4 boxes=1/1 ramps=1/1",
                (string)Invoke(level, arenaType, "GetSetupReport"));

            // An offset registration leaves no residue: Reapply restores the id base 0.
            Invoke(level, arenaType, "Register", false, 5, 3);
            Invoke(level, arenaType, "Reapply");
            CollectionAssert.AreEquivalent(new[] { 500, 1 }, StaticSurfaceIds());
        }

        [Test]
        public void TheCapacityCheckCountsSolidsAlreadyInTheTable()
        {
            // Appended hosts share the 256-slot table: 255 held + 2 asked = one refusal.
            int capacity = (int)Invoke(motor, motorType, "GetStaticObbCapacity");
            Assert.AreEqual(256, capacity);
            for (int i = 0; i < capacity - 1; i++)
            {
                Assert.IsTrue((bool)Invoke(motor, motorType, "AddObb",
                    new Vector3(i * 3f, 0f, 100f), new Vector3(1f, 0f, 0f),
                    new Vector3(0f, 1f, 0f), new Vector3(0f, 0f, 1f),
                    new Vector3(0.5f, 0.5f, 0.5f), 1, 0));
            }

            Component cell = NewArena("cell", new[] { Box("one", 0f), Box("two", 4f) }, null, 0);
            // The up-front capacity check names the overflow once (255 held + 2 asked = 257).
            LogAssert.Expect(LogType.Error, new Regex(@"\[arena\] static OBB table overflow: 255 already held \+ 2 from this host .* > 256 slots"));
            Invoke(cell, arenaType, "Register", false, 0, 0);

            Assert.AreEqual(1, (int)Invoke(cell, arenaType, "GetRegisteredObbCount"),
                "one of the two fits");
            Assert.AreEqual(1, (int)Invoke(cell, arenaType, "GetObbOverflowCount"),
                "the other is refused with the overflow code, never dropped silently");
        }

        // ---- the field's own budget ---------------------------------------------

        [Test]
        public void TheWholeSandboxFitsTheObbTableAndThePlaneTable()
        {
            int capacity = 256;
            int obbs = 0;
            string root = ResolveLevelsRoot();
            Assert.IsNotNull(root, LevelsRepoRelative + " not found above the Unity project");
            for (int i = 0; i < WiredCells.Length; i++)
            {
                string path = Path.Combine(Path.Combine(root, WiredCells[i]), "placement.json");
                Assert.IsTrue(File.Exists(path), path);
                string text = File.ReadAllText(path);
                // One OBB per role=box and per role=ramp structure; ground and boundary are planes.
                obbs += Regex.Matches(text, "\"role\":\\s*\"box\"").Count;
                obbs += Regex.Matches(text, "\"role\":\\s*\"ramp\"").Count;
            }

            string frame = File.ReadAllText(Path.Combine(Path.Combine(root, "SANDBOX"), "placement.json"));
            for (int i = 0; i < WiredCells.Length; i++)
            {
                string text = File.ReadAllText(Path.Combine(Path.Combine(root, WiredCells[i]), "placement.json"));
                Assert.AreEqual(0, Regex.Matches(text, "\"role\":\\s*\"ground\"").Count,
                    WiredCells[i] + " carries no floor of its own");
                Assert.AreEqual(0, Regex.Matches(text, "\"role\":\\s*\"boundary\"").Count,
                    WiredCells[i] + " carries no wall of its own");
                StringAssert.Contains("\"frame\": \"shared\"", text, WiredCells[i]);
            }

            Assert.AreEqual(0, Regex.Matches(frame, "\"role\":\\s*\"box\"").Count, "the frame has no boxes");
            Assert.AreEqual(0, Regex.Matches(frame, "\"role\":\\s*\"ramp\"").Count, "the frame has no ramps");
            int framePlanes = Regex.Matches(frame, "\"role\":\\s*\"boundary\"").Count;
            Assert.AreEqual(4, framePlanes, "the frame is one floor and 4 outer planes");

            Assert.AreEqual(21, obbs, "13 wired cells, counted from their placements");
            Assert.LessOrEqual(obbs, capacity, "static OBB table (256)");
            // Even if the outer wall were modelled as solids instead of planes it would still fit.
            Assert.LessOrEqual(obbs + framePlanes + 1, capacity);
            Assert.LessOrEqual(framePlanes, 8, "plane table (8): the cells' walls are not registered");
        }

        [Test]
        public void TheDriverAndTheCellCarryOnlyTheSandbox()
        {
            Type driver = FindType("VRCKatamari.Labs.PhysicsTestField.PhysicsTestFieldDriver");
            Type cell = FindType("VRCKatamari.Labs.PhysicsTestField.PhysicsTestCell");
            Assert.IsNotNull(driver, "PhysicsTestFieldDriver type not loaded");
            Assert.IsNotNull(cell, "PhysicsTestCell type not loaded");

            string[] fields = { "sandboxFrame", "sandboxSpawn", "sandboxSpawnHeadingDegrees" };
            for (int i = 0; i < fields.Length; i++)
            {
                FieldInfo field = driver.GetField(fields[i], BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.IsNotNull(field, "driver." + fields[i] + " is missing");
                Assert.IsTrue(field.IsDefined(typeof(SerializeField), false),
                    "driver." + fields[i] + " must be serialized (the importer writes it)");
            }

            // One mechanism: no walk-around, no isolated-cell mode, no one-cell registration.
            Assert.IsNull(driver.GetField("walkAroundActivation", BindingFlags.Instance | BindingFlags.NonPublic));
            Assert.IsNull(driver.GetField("isolatedCellMode", BindingFlags.Instance | BindingFlags.NonPublic));
            Assert.IsNull(cell.GetMethod("ContainsPoint", Any));
            Assert.IsNull(cell.GetMethod("ApplyRegistration", Any), "no one-cell registration");

            Assert.IsNotNull(cell.GetMethod("ApplySandboxRegistration", Any), "cell sandbox registration");
            MethodInfo register = arenaType.GetMethod("Register", Any);
            Assert.IsNotNull(register, "LevelArenaHost.Register");
            Assert.AreEqual(3, register.GetParameters().Length);
        }

        // ---- the importer rule for a cell on a shared frame ---------------------

        [Test]
        public void ASharedFrameCellCarriesNoFloorAndNoWall()
        {
            Type importer = FindType("VRCKatamari.Editor.LevelBuild.PlacementImporter");
            Assert.IsNotNull(importer, "PlacementImporter type not loaded");
            MethodInfo rule = importer.GetMethod("FrameRuleViolation", BindingFlags.Public | BindingFlags.Static);
            Assert.IsNotNull(rule, "PlacementImporter.FrameRuleViolation is missing");

            Assert.IsNull(rule.Invoke(null, new object[] { "shared", 0, 0 }));
            StringAssert.Contains("must not carry", (string)rule.Invoke(null, new object[] { "shared", 1, 0 }));
            StringAssert.Contains("must not carry", (string)rule.Invoke(null, new object[] { "shared", 0, 4 }));
            // Every other document is unchanged: exactly one role=ground.
            Assert.IsNull(rule.Invoke(null, new object[] { null, 1, 4 }));
            Assert.IsNull(rule.Invoke(null, new object[] { "own", 1, 0 }));
            StringAssert.Contains("exactly one role=ground", (string)rule.Invoke(null, new object[] { null, 0, 0 }));
            StringAssert.Contains("exactly one role=ground", (string)rule.Invoke(null, new object[] { "own", 2, 0 }));
            StringAssert.Contains("must be 'own' or 'shared'", (string)rule.Invoke(null, new object[] { "nope", 1, 0 }));
        }

        // ---- helpers -----------------------------------------------------------

        private Component NewArena(string name, BoxCollider[] boxes, BoxCollider[] ramps, int walls)
        {
            GameObject host = new GameObject("SandboxRegistrationTests_" + name);
            created.Add(host);
            Component arena = host.AddComponent(arenaType);
            Transform[] wallTransforms = null;
            if (walls > 0)
            {
                wallTransforms = new Transform[walls];
                for (int i = 0; i < walls; i++)
                {
                    GameObject wall = new GameObject(name + "_wall" + i);
                    created.Add(wall);
                    wall.transform.position = new Vector3(50f * (i + 1), 0f, 0f);
                    wall.transform.rotation = Quaternion.Euler(0f, 90f * i, 0f);
                    wallTransforms[i] = wall.transform;
                }
            }

            SetField(arena, "motor", motor);
            SetField(arena, "boundaryWalls", wallTransforms);
            SetField(arena, "staticBoxes", boxes);
            SetField(arena, "rampBoxes", ramps);
            SetField(arena, "logSetupLine", false);
            SetField(arena, "autoBeginPlaying", false);
            Physics.SyncTransforms();
            return arena;
        }

        private BoxCollider Box(string name, float x)
        {
            GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            created.Add(go);
            go.name = name;
            go.transform.position = new Vector3(x, 0.5f, 0f);
            go.transform.localScale = new Vector3(2f, 1f, 1f);
            Physics.SyncTransforms();
            return go.GetComponent<BoxCollider>();
        }

        /// <summary>A board tilted 20 degrees (local +Y walking normal, +Z up the run).</summary>
        private BoxCollider Ramp(string name, float x)
        {
            GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            created.Add(go);
            go.name = name;
            float radians = 20f * Mathf.Deg2Rad;
            Vector3 normal = new Vector3(0f, Mathf.Cos(radians), -Mathf.Sin(radians));
            Vector3 forward = new Vector3(0f, Mathf.Sin(radians), Mathf.Cos(radians));
            go.transform.rotation = Quaternion.LookRotation(forward, normal);
            go.transform.position = new Vector3(x, 0.2f, 3f) - normal * 0.02f;
            go.transform.localScale = new Vector3(1.2f, 0.04f, 1f);
            Physics.SyncTransforms();
            return go.GetComponent<BoxCollider>();
        }

        private int[] StaticSurfaceIds()
        {
            FieldInfo field = motorType.GetField("staticObbSurfaceId", Any);
            Assert.IsNotNull(field, "staticObbSurfaceId field not found");
            int[] all = (int[])field.GetValue(motor);
            int count = (int)Invoke(motor, motorType, "GetStaticObbCount");
            int[] ids = new int[count];
            for (int i = 0; i < count; i++) ids[i] = all[i];
            return ids;
        }

        private int GetIntField(string fieldName)
        {
            FieldInfo field = motorType.GetField(fieldName, Any);
            Assert.IsNotNull(field, fieldName + " field not found");
            return (int)field.GetValue(motor);
        }

        private void SetField(Component target, string name, object value)
        {
            FieldInfo field = target.GetType().GetField(name, Any);
            Assert.IsNotNull(field, name + " field not found");
            field.SetValue(target, value);
        }

        private static object Invoke(Component target, Type type, string name, params object[] args)
        {
            MethodInfo method = type.GetMethod(name, Any);
            Assert.IsNotNull(method, name + " not found");
            return method.Invoke(target, args);
        }

        private static string ResolveLevelsRoot()
        {
            string projectRoot = Path.GetDirectoryName(Application.dataPath) ??
                                 Directory.GetCurrentDirectory();
            string directory = projectRoot;
            for (int i = 0; i < 6 && !string.IsNullOrEmpty(directory); i++)
            {
                string candidate = Path.GetFullPath(Path.Combine(directory, "..", LevelsRepoRelative));
                if (Directory.Exists(candidate)) return candidate;
                candidate = Path.GetFullPath(Path.Combine(directory, LevelsRepoRelative));
                if (Directory.Exists(candidate)) return candidate;
                directory = Path.GetDirectoryName(directory);
            }

            return null;
        }

        private static Type FindType(string fullName)
        {
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type candidate = assembly.GetType(fullName);
                if (candidate != null) return candidate;
            }

            return null;
        }
    }
}
