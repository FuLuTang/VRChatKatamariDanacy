using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace VRCKatamari.Tests
{
    /// <summary>
    /// Motor-level checks for the arena's contact-v2 geometry bake (CONTACT_MODEL_V2
    /// §1.4/§5.7/§7.4, stage S4b): one true OBB per static box, ramp and curved-ramp slab,
    /// registered through <c>AddObb</c> while the motor reports v2; the pre-S4
    /// <c>AddBox</c>/<c>AddRamp</c> calls in every other mode; the §7.4 double-bake check;
    /// the shear fallback; and an explicit, counted refusal when the motor's 256-slot table
    /// is full.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Reflection only, for the same reason as <c>ContactModelV2Tests</c>: this test
    /// assembly does not reference UdonSharp.Runtime, so a behaviour type cannot appear in a
    /// field or a signature. Both components are added by <see cref="Type"/> and driven
    /// through their methods, and the motor's OBB table is read through its private arrays
    /// exactly as a reviewer would inspect it in the debugger.
    /// </para>
    /// <para>
    /// What these tests do NOT claim: no scene, no Play Mode, no frame time, no
    /// levelbuild/import. They pin the migration checkpoint that matters most - the geometry
    /// count and the box numbers a v1 scene already validated - and the two failure paths
    /// (§7.4 mismatch, sheared parent) that are otherwise invisible until a play test.
    /// </para>
    /// </remarks>
    public class LevelArenaHostObbTests
    {
        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic |
            BindingFlags.Public;
        private const float Tolerance = 1e-5f;
        // §7.4's own number: axis-aligned OBB and bounds AABB agree to 1e-6 m.
        private const float StrictTolerance = 1e-6f;

        private GameObject motorHost;
        private GameObject arenaHost;
        private GameObject geometryRoot;
        private Component motor;
        private Component arena;
        private Type motorType;
        private Type arenaType;

        [SetUp]
        public void SetUp()
        {
            motorType = FindType("VRCKatamari.BallMotor.KatamariBallMotor");
            arenaType = FindType("VRCKatamari.Story.LevelArenaHost");
            Assert.IsNotNull(motorType, "KatamariBallMotor type not loaded");
            Assert.IsNotNull(arenaType, "LevelArenaHost type not loaded");

            motorHost = new GameObject("LevelArenaHostObbTestsMotor");
            motor = motorHost.AddComponent(motorType);
            Invoke(motor, motorType, "Start");

            arenaHost = new GameObject("LevelArenaHostObbTestsArena");
            arena = arenaHost.AddComponent(arenaType);
            geometryRoot = new GameObject("LevelArenaHostObbTestsGeometry");
        }

        [TearDown]
        public void TearDown()
        {
            if (geometryRoot != null) UnityEngine.Object.DestroyImmediate(geometryRoot);
            if (arenaHost != null) UnityEngine.Object.DestroyImmediate(arenaHost);
            if (motorHost != null) UnityEngine.Object.DestroyImmediate(motorHost);
        }

        [Test]
        public void V2BakesOneTrueObbPerBoxAndPerSlab()
        {
            // One upright deck plus two tilted boards, i.e. exactly what the importer emits
            // for a box and for two segments of a curved ramp.
            BoxCollider deck = CreateBox("deck", new Vector3(2f, 0.5f, -1f),
                new Vector3(2f, 1f, 1f), Quaternion.identity);
            BoxCollider slab0 = CreateTiltedBoard("curve_00", new Vector3(0f, 0.2f, 0f),
                30f, 1.2f, 0.04f, 1f);
            BoxCollider slab1 = CreateTiltedBoard("curve_01", new Vector3(0f, 0.6f, 1f),
                55f, 1.2f, 0.04f, 1f);
            ConfigureArena(true, new BoxCollider[] { deck }, new BoxCollider[] { slab0, slab1 });

            Invoke(arena, arenaType, "Setup");

            Assert.IsTrue((bool)Invoke(arena, arenaType, "IsObbBakeActive"),
                "v2 + useObbBake takes the AddObb path");
            // One path per mode: the v1 tables are empty and the v2 table is not doubled.
            Assert.AreEqual(0, GetIntField("boxCount"),
                "AddBox is not called in the v2 OBB path");
            Assert.AreEqual(0, (int)Invoke(motor, motorType, "GetRampCount"),
                "AddRamp is not called in the v2 OBB path");
            Assert.AreEqual(3, (int)Invoke(motor, motorType, "GetStaticObbCount"),
                "one static OBB per box and per ramp/curve slab, and no duplicate");
            Assert.AreEqual(3, (int)Invoke(arena, arenaType, "GetRegisteredObbCount"),
                "the arena counts what the motor accepted");
            Assert.AreEqual(1, (int)Invoke(arena, arenaType, "GetRegisteredBoxCount"),
                "the host's own registration counters still count the registrations");
            Assert.AreEqual(2, (int)Invoke(arena, arenaType, "GetRegisteredRampCount"),
                "a curved ramp counts as its slab colliders");
            Assert.AreEqual(4, (int)Invoke(arena, arenaType, "GetRegisteredSupportCount"),
                "ground + one deck top + two slab tops");
            Assert.AreEqual(0, (int)Invoke(arena, arenaType, "GetObbRefusalCount"));
            Assert.AreEqual(0, (int)Invoke(arena, arenaType, "GetObbOverflowCount"));

            // Surface ids stay in the v1 space: ramps are registered first (500, 501 - the
            // ramp base moved off 10 when the static box table grew to 64, and off 1000
            // which the v2 solver's boundary planes own), the box after them (1).
            // Roles: 1 = slab, 0 = box (CONTACT_MODEL_V2 §2.1).
            int[] surfaceIds = GetArray<int>("staticObbSurfaceId");
            int[] roles = GetArray<int>("staticObbRole");
            CollectionAssert.AreEqual(new[] { 500, 501, 1 }, surfaceIds,
                "ramp slabs keep FirstRampSurfaceId+i and boxes keep 1+i");
            CollectionAssert.AreEqual(new[] { 1, 1, 0 }, roles, "slab, slab, box");

            // The deck's bake is the bounds box to the last digit (§7.4).
            Vector3[] centers = GetArray<Vector3>("staticObbCenter");
            Vector3[] halfExtents = GetArray<Vector3>("staticObbHalfExtents");
            AssertVectorStrict(new Vector3(2f, 0.5f, -1f), centers[2], "deck centre");
            AssertVectorStrict(new Vector3(1f, 0.5f, 0.5f), halfExtents[2], "deck half extents");
            Assert.AreEqual(1, (int)Invoke(arena, arenaType, "GetDoubleBakeCheckCount"),
                "only the axis-aligned deck is compared by the §7.4 check");
            Assert.AreEqual(0, (int)Invoke(arena, arenaType, "GetDoubleBakeMismatchCount"),
                "the two bakes agree for axis-aligned geometry");

            // A slab's +Y is its walking normal: 30 deg and 55 deg above the horizon.
            Vector3[] axisY = GetArray<Vector3>("staticObbAxisY");
            AssertVector(new Vector3(0f, Mathf.Cos(30f * Mathf.Deg2Rad),
                -Mathf.Sin(30f * Mathf.Deg2Rad)), axisY[0], "slab 0 normal");
            AssertVector(new Vector3(0f, Mathf.Cos(55f * Mathf.Deg2Rad),
                -Mathf.Sin(55f * Mathf.Deg2Rad)), axisY[1], "slab 1 normal");
            AssertVector(new Vector3(0.6f, 0.02f, 0.5f), halfExtents[0], "slab 0 half extents");

            string report = (string)Invoke(arena, arenaType, "GetSetupReport");
            StringAssert.StartsWith("[arena] planes=0/0 boxes=1/1 ramps=2/2", report,
                "the prefix the tools and docs parse is unchanged");
            StringAssert.Contains(" obbs=3/3", report);
            StringAssert.Contains(" obbBake=obb", report);
        }

        [Test]
        public void V1AndTheRollbackPathKeepTheOldCallsAndTheMotorMirror()
        {
            BoxCollider deck = CreateBox("deck", new Vector3(0f, 0.5f, 0f),
                new Vector3(2f, 1f, 2f), Quaternion.identity);
            BoxCollider slab = CreateTiltedBoard("ramp", new Vector3(0f, 0.2f, 3f), 30f, 1.2f,
                0.04f, 1f);

            // 1) v1 motor: the pre-S4 path, unchanged, and the motor mirrors both into its
            //    axis-aligned OBB table (S3 §7.5.3) - count 2, never 4.
            ConfigureArena(false, new BoxCollider[] { deck }, new BoxCollider[] { slab });
            Invoke(arena, arenaType, "Setup");

            Assert.IsFalse((bool)Invoke(arena, arenaType, "IsObbBakeActive"),
                "a v1 motor never takes the AddObb path");
            Assert.AreEqual(1, GetIntField("boxCount"), "AddBox is used");
            Assert.AreEqual(1, (int)Invoke(motor, motorType, "GetRampCount"), "AddRamp is used");
            Assert.AreEqual(2, (int)Invoke(motor, motorType, "GetStaticObbCount"),
                "the motor's mirror registers each solid exactly once");
            Assert.AreEqual(0, (int)Invoke(arena, arenaType, "GetRegisteredObbCount"),
                "the arena itself registered no OBB in v1 mode");

            // 2) v2 motor with the bake switched off: the design's rollback (§7.1). Same old
            //    calls, same mirrored table, no second registration. A fresh arena component,
            //    because Setup is once per component (that is the shipped contract).
            NewArena();
            BoxCollider deck2 = CreateBox("deck2", new Vector3(0f, 0.5f, 0f),
                new Vector3(2f, 1f, 2f), Quaternion.identity);
            ConfigureArena(true, new BoxCollider[] { deck2 }, new BoxCollider[] { slab });
            SetField(arena, "useObbBake", false);
            Invoke(arena, arenaType, "Setup");

            Assert.IsFalse((bool)Invoke(arena, arenaType, "IsObbBakeActive"),
                "useObbBake=false is the AABB rollback");
            Assert.AreEqual(1, GetIntField("boxCount"), "AddBox again");
            Assert.AreEqual(1, (int)Invoke(motor, motorType, "GetRampCount"), "AddRamp again");
            Assert.AreEqual(2, (int)Invoke(motor, motorType, "GetStaticObbCount"),
                "still one OBB per solid, so a scene never registers one twice");
        }

        [Test]
        public void RotatedBoxKeepsItsOwnAxesAndItsAuthoredExtents()
        {
            // A 45 deg yawed deck: the eight-corner OBB is the turned box (half extents are
            // the authored halves), NOT the inflated world AABB a v1 registration would see.
            BoxCollider deck = CreateBox("turned_deck", new Vector3(0f, 0.5f, 0f),
                new Vector3(2f, 1f, 4f), Quaternion.AngleAxis(45f, Vector3.up));
            ConfigureArena(true, new BoxCollider[] { deck }, null);

            Invoke(arena, arenaType, "Setup");

            Vector3[] halfExtents = GetArray<Vector3>("staticObbHalfExtents");
            Vector3[] axisY = GetArray<Vector3>("staticObbAxisY");
            AssertVector(new Vector3(1f, 0.5f, 2f), halfExtents[0],
                "the authored half extents survive the rotation");
            AssertVector(new Vector3(0f, 1f, 0f), axisY[0], "a yawed box keeps Y up");
            // The world AABB of this box is wider than 2 on both X and Z; the OBB is not.
            Bounds bounds = deck.bounds;
            Assert.Greater(bounds.size.x, 3.9f, "the bounds AABB is the inflated shape");
            Assert.AreEqual(0, (int)Invoke(arena, arenaType, "GetDoubleBakeCheckCount"),
                "a rotated box is not compared against its bounds AABB");
        }

        [Test]
        public void ShearedParentIsRefusedWithAReasonCodeAndTheBoundsBoxIsUsed()
        {
            // A rotated child under a non-uniformly scaled parent: its eight world corners are
            // a parallelogram, not a box. The arena must refuse the OBB (§1.4) and register
            // the bounds AABB instead, with a reason code that says which.
            GameObject parent = new GameObject("shear_parent");
            parent.transform.SetParent(geometryRoot.transform, false);
            parent.transform.localScale = new Vector3(2f, 1f, 1f);
            BoxCollider sheared = CreateBox("sheared", Vector3.zero, Vector3.one,
                Quaternion.AngleAxis(45f, Vector3.forward), parent.transform);

            ConfigureArena(true, new BoxCollider[] { sheared }, null);
            Invoke(arena, arenaType, "Setup");

            Assert.AreEqual(1, (int)Invoke(arena, arenaType, "GetRegisteredObbCount"),
                "the fallback box is still registered - geometry is never dropped");
            Assert.AreEqual(1, (int)Invoke(arena, arenaType, "GetLastRefusalCode"),
                "1 = shear");
            StringAssert.Contains("sheared", (string)Invoke(arena, arenaType, "GetLastRefusalReason"));

            // The fallback IS the collider's bounds box: same numbers, world-aligned axes, and
            // wider than the authored 0.5 m half extent (so it cannot be the reconstruction).
            Vector3[] halfExtents = GetArray<Vector3>("staticObbHalfExtents");
            Vector3[] axisX = GetArray<Vector3>("staticObbAxisX");
            Bounds bounds = sheared.bounds;
            AssertVectorStrict(bounds.extents, halfExtents[0],
                "the sheared box falls back to its bounds AABB");
            AssertVector(new Vector3(1f, 0f, 0f), axisX[0], "the fallback is world aligned");
            Assert.Greater(halfExtents[0].x, 0.6f,
                "the AABB is wider than the authored box (the corners are not a box)");
            Assert.AreEqual(0, (int)Invoke(arena, arenaType, "GetDoubleBakeMismatchCount"),
                "shear is not reported as a §7.4 mismatch");
        }

        [Test]
        public void FullTableRefusesEachSolidWithAnOverflowCode()
        {
            // Fill the motor's 256-slot table, then register one arena box: the refusal must
            // be counted, named, and never silently dropped (§5.4).
            int capacity = (int)Invoke(motor, motorType, "GetStaticObbCapacity");
            for (int i = 0; i < capacity; i++)
            {
                object added = Invoke(motor, motorType, "AddObb",
                    new Vector3(i * 3f, 0f, 0f), new Vector3(1f, 0f, 0f),
                    new Vector3(0f, 1f, 0f), new Vector3(0f, 0f, 1f),
                    new Vector3(0.5f, 0.5f, 0.5f), 1, 0);
                Assert.IsTrue((bool)added, "filling the table must succeed up to its capacity");
            }

            BoxCollider deck = CreateBox("deck", new Vector3(0f, 0.5f, 0f),
                new Vector3(1f, 1f, 1f), Quaternion.identity);
            ConfigureArena(true, new BoxCollider[] { deck }, null);
            Invoke(arena, arenaType, "Setup");

            Assert.AreEqual(0, (int)Invoke(arena, arenaType, "GetRegisteredObbCount"),
                "the overflow box is refused, not written over another solid");
            Assert.AreEqual(1, (int)Invoke(arena, arenaType, "GetObbOverflowCount"));
            Assert.AreEqual(0, (int)Invoke(arena, arenaType, "GetRegisteredBoxCount"),
                "a refused registration is not counted as registered");
            Assert.AreEqual(3, (int)Invoke(arena, arenaType, "GetLastRefusalCode"),
                "3 = OBB capacity");
            string report = (string)Invoke(arena, arenaType, "GetSetupReport");
            StringAssert.Contains(" obbs=0/1", report);
            StringAssert.Contains(" obbRefused=1", report);
            StringAssert.Contains(" obbOverflow=1", report);
        }

        [Test]
        public void ReapplyReregistersEachSolidExactlyOnce()
        {
            // The physics test field's cell-switch seam: ClearObstacles drops the OBB table
            // too, so the arena must re-add exactly the scene's solids, not twice as many.
            BoxCollider deck = CreateBox("deck", new Vector3(0f, 0.5f, 0f),
                new Vector3(2f, 1f, 1f), Quaternion.identity);
            BoxCollider slab = CreateTiltedBoard("ramp", new Vector3(0f, 0.2f, 2f), 20f, 1.2f,
                0.04f, 1f);
            ConfigureArena(true, new BoxCollider[] { deck }, new BoxCollider[] { slab });
            Invoke(arena, arenaType, "Setup");
            Assert.AreEqual(2, (int)Invoke(motor, motorType, "GetStaticObbCount"));

            Invoke(arena, arenaType, "Reapply");

            Assert.AreEqual(2, (int)Invoke(motor, motorType, "GetStaticObbCount"),
                "Reapply re-registers the same two solids once each");
            Assert.AreEqual(2, (int)Invoke(arena, arenaType, "GetRegisteredObbCount"));
        }

        // ---- helpers -----------------------------------------------------------

        private void ConfigureArena(bool v2, BoxCollider[] boxes, BoxCollider[] ramps)
        {
            Invoke(motor, motorType, "SetContactModelV2Enabled", v2);
            SetField(arena, "motor", motor);
            SetField(arena, "staticBoxes", boxes);
            SetField(arena, "rampBoxes", ramps);
            SetField(arena, "boundaryWalls", null);
            // No console lines from a unit test, and no round state: the tests only inspect
            // the registration tables.
            SetField(arena, "logSetupLine", false);
            SetField(arena, "autoBeginPlaying", false);
            // Same reason as CreateBox: the arena reads Collider.bounds in Setup.
            Physics.SyncTransforms();
        }

        /// <summary>Drops the motor's tables and attaches a fresh arena (Setup is once-only).</summary>
        private void NewArena()
        {
            if (arenaHost != null) UnityEngine.Object.DestroyImmediate(arenaHost);
            Invoke(motor, motorType, "ClearObstacles");
            arenaHost = new GameObject("LevelArenaHostObbTestsArena2");
            arena = arenaHost.AddComponent(arenaType);
        }

        private BoxCollider CreateBox(string name, Vector3 position, Vector3 size,
            Quaternion rotation)
        {
            return CreateBox(name, position, size, rotation, null);
        }

        /// <summary>
        /// A unit cube with a BoxCollider, scaled to <paramref name="size"/>: the shape the
        /// importer authors for every structure (collider size 1, the scale is the size).
        /// </summary>
        private BoxCollider CreateBox(string name, Vector3 position, Vector3 size,
            Quaternion rotation, Transform parent)
        {
            GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            if (parent != null)
            {
                go.transform.SetParent(parent, false);
                go.transform.localPosition = position;
                go.transform.localRotation = rotation;
            }
            else
            {
                go.transform.SetParent(geometryRoot.transform, false);
                go.transform.position = position;
                go.transform.rotation = rotation;
            }
            go.transform.localScale = size;
            // Collider.bounds comes from the physics scene, and Physics.autoSyncTransforms is
            // off by default; without this an EditMode test reads the bounds the primitive had
            // when it was created (scale 1), not the authored ones. Play Mode is unaffected.
            Physics.SyncTransforms();
            BoxCollider collider = go.GetComponent<BoxCollider>();
            Assert.IsNotNull(collider, "the cube primitive carries a BoxCollider");
            return collider;
        }

        /// <summary>
        /// A board tilted <paramref name="slopeDeg"/> degrees: local +Y is the walking normal
        /// and local +Z runs up the slope, which is the convention both a straight ramp and a
        /// curved ramp's slab segments use (LEVEL_AUTHORING, CONTACT_MODEL_V2 §5.7).
        /// </summary>
        private BoxCollider CreateTiltedBoard(string name, Vector3 walkingCenter, float slopeDeg,
            float width, float thickness, float length)
        {
            float radians = slopeDeg * Mathf.Deg2Rad;
            Vector3 normal = new Vector3(0f, Mathf.Cos(radians), -Mathf.Sin(radians));
            Vector3 forward = new Vector3(0f, Mathf.Sin(radians), Mathf.Cos(radians));
            Quaternion rotation = Quaternion.LookRotation(forward, normal);
            // The board hangs below the walking line: its top face is the walking plane.
            Vector3 position = walkingCenter - normal * (thickness * 0.5f);
            return CreateBox(name, position, new Vector3(width, thickness, length), rotation);
        }

        private int GetIntField(string fieldName)
        {
            FieldInfo field = motorType.GetField(fieldName, Any);
            Assert.IsNotNull(field, fieldName + " field not found");
            return (int)field.GetValue(motor);
        }

        /// <summary>
        /// The STATIC prefix of one of the motor's union OBB arrays.
        /// </summary>
        /// <remarks>
        /// Since the prop block landed, <c>staticObb*</c> is one fixed 256+12 array with the
        /// statics first and the props appended; reading the raw array would hand a 268-long
        /// buffer to an assertion about three entries.
        /// </remarks>
        private T[] GetArray<T>(string fieldName)
        {
            FieldInfo field = motorType.GetField(fieldName, Any);
            Assert.IsNotNull(field, fieldName + " field not found");
            T[] all = (T[])field.GetValue(motor);
            int staticCount = (int)Invoke(motor, motorType, "GetStaticObbCount");
            T[] prefix = new T[staticCount];
            for (int i = 0; i < staticCount; i++) prefix[i] = all[i];
            return prefix;
        }

        private void SetField(Component target, string name, object value)
        {
            Type type = target == arena ? arenaType : motorType;
            FieldInfo field = type.GetField(name, Any);
            Assert.IsNotNull(field, name + " field not found");
            field.SetValue(target, value);
        }

        private object Invoke(Component target, Type type, string name, params object[] args)
        {
            MethodInfo method = type.GetMethod(name, Any);
            Assert.IsNotNull(method, name + " not found");
            return method.Invoke(target, args);
        }

        private static Type FindType(string fullName)
        {
            Type found = Type.GetType(fullName + ", VRCKatamari.Runtime");
            if (found != null) return found;
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type candidate = assembly.GetType(fullName);
                if (candidate != null) return candidate;
            }
            return null;
        }

        private static void AssertVector(Vector3 expected, Vector3 actual, string message)
        {
            AssertVectorWithin(expected, actual, Tolerance, message);
        }

        private static void AssertVectorStrict(Vector3 expected, Vector3 actual, string message)
        {
            AssertVectorWithin(expected, actual, StrictTolerance, message);
        }

        private static void AssertVectorWithin(Vector3 expected, Vector3 actual, float tolerance,
            string message)
        {
            Assert.AreEqual(expected.x, actual.x, tolerance, message + " (x)");
            Assert.AreEqual(expected.y, actual.y, tolerance, message + " (y)");
            Assert.AreEqual(expected.z, actual.z, tolerance, message + " (z)");
        }
    }
}
