using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace VRCKatamari.Tests
{
    /// <summary>
    /// A prop that has a same-id static solid (the reference level's big scenery) takes that solid
    /// out of the motor when it is rolled up, and a field reset puts it back
    /// (<c>LevelArenaHost.RemoveSolidsOf</c> / <c>RestoreRemovedSolids</c>,
    /// <c>KatamariBallMotor.RemoveStaticObb</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Before this, rolling the prop up left the solid behind as an invisible wall: the prop is the
    /// only thing the pickup host removes, and the static OBB table had no per-entry removal.
    /// </para>
    /// <para>
    /// Pinned here: no block where the prop stood; the other solids, their surface ids and the
    /// motor's prop block keep their values (the table layout is "static prefix, then the prop
    /// block"); a restore brings back exactly the removed entries and never twice; two arena hosts
    /// sharing one motor (the hub) do not touch each other's solids; and a host whose table was
    /// dropped by a level switch neither removes from nor restores into the new level's table.
    /// Reflection only, like <c>LevelArenaHostObbTests</c>: the test assembly does not reference
    /// UdonSharp.Runtime.
    /// </para>
    /// </remarks>
    public class LevelArenaHostTwinSolidTests
    {
        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic |
            BindingFlags.Public;

        private GameObject motorHost;
        private GameObject geometryRoot;
        private Component motor;
        private Type motorType;
        private Type arenaType;
        private readonly System.Collections.Generic.List<GameObject> made =
            new System.Collections.Generic.List<GameObject>();

        [SetUp]
        public void SetUp()
        {
            motorType = FindType("VRCKatamari.BallMotor.KatamariBallMotor");
            arenaType = FindType("VRCKatamari.Story.LevelArenaHost");
            Assert.IsNotNull(motorType, "KatamariBallMotor type not loaded");
            Assert.IsNotNull(arenaType, "LevelArenaHost type not loaded");
            motorHost = new GameObject("TwinSolidTestsMotor");
            motor = motorHost.AddComponent(motorType);
            Invoke(motor, motorType, "Start");
            geometryRoot = new GameObject("TwinSolidTestsGeometry");
        }

        [TearDown]
        public void TearDown()
        {
            for (int i = 0; i < made.Count; i++)
            {
                if (made[i] != null) UnityEngine.Object.DestroyImmediate(made[i]);
            }
            made.Clear();
            if (geometryRoot != null) UnityEngine.Object.DestroyImmediate(geometryRoot);
            if (motorHost != null) UnityEngine.Object.DestroyImmediate(motorHost);
        }

        [Test]
        public void RollingUpTheTwinLeavesNoBlockWhereItStood()
        {
            Component arena = NewArena(false, 0, "lantern_001", "");
            Vector3 lantern = new Vector3(3f, 0.5f, 0f);

            Assert.AreEqual(2, StaticCount(), "twin and plain solid are both registered");
            Assert.AreEqual(1, NearCountAt(lantern), "the twin blocks the ball while the prop stands");

            Assert.AreEqual(1, RemoveSolidsOf(arena, "lantern_001"));

            Assert.AreEqual(1, StaticCount());
            Assert.AreEqual(0, NearCountAt(lantern), "no invisible wall where the prop stood");
            Assert.AreEqual(1, NearCountAt(new Vector3(-3f, 0.5f, 0f)), "the plain solid stays");
        }

        [Test]
        public void OtherSolidsSurfaceIdsAndThePropBlockKeepTheirValues()
        {
            Component arena = NewArena(false, 0, "lantern_001", "");
            // A prop block of two: the host's scan writes surface ids 100 + block slot.
            Vector3[] centers = { new Vector3(0f, 1f, 5f), new Vector3(1f, 1f, 6f) };
            Assert.IsTrue(SetPropBlock(centers));
            Assert.AreEqual(2, PropCount());
            int plainIdBefore = StaticSurfaceId(1);
            Vector3 plainCenterBefore = StaticCenter(1);

            Assert.AreEqual(1, RemoveSolidsOf(arena, "lantern_001"));

            Assert.AreEqual(1, StaticCount());
            Assert.AreEqual(plainIdBefore, StaticSurfaceId(0), "the surviving solid keeps its id");
            AssertVector(plainCenterBefore, StaticCenter(0));
            Assert.AreEqual(2, PropCount(), "the prop block keeps its count");
            Assert.AreEqual(2, (int)Invoke(motor, motorType, "GetPropObbsCount"));
            // The block now starts right after the shorter static prefix, in the same order.
            Assert.AreEqual(100, StaticSurfaceId(1));
            Assert.AreEqual(101, StaticSurfaceId(2));
            AssertVector(centers[0], StaticCenter(1));
            AssertVector(centers[1], StaticCenter(2));
            Assert.AreEqual(0, StaticSurfaceId(3), "the vacated slot is reset, not a stale copy");
        }

        [Test]
        public void ARemoveThatMatchesNothingChangesNothing()
        {
            Component arena = NewArena(false, 0, "lantern_001", "");

            Assert.AreEqual(0, RemoveSolidsOf(arena, "no_such_prop"));
            Assert.AreEqual(0, RemoveSolidsOf(arena, ""), "a plain solid has an empty id and is never named");
            Assert.AreEqual(0, RemoveSolidsOf(arena, null));
            Assert.AreEqual(2, StaticCount());

            Assert.AreEqual(1, RemoveSolidsOf(arena, "lantern_001"));
            Assert.AreEqual(0, RemoveSolidsOf(arena, "lantern_001"), "a second remove is a no-op");
            Assert.AreEqual(1, StaticCount());
        }

        [Test]
        public void ASplitSolidTakesAllItsPartsOut()
        {
            Component arena = NewArenaFromBoxes(false, 0, new[]
            {
                Box("door_a", new Vector3(3f, 0.5f, 0f)), Box("door_b", new Vector3(3f, 0.5f, 2f)),
                Box("plain", new Vector3(-3f, 0.5f, 0f))
            }, new[] { "door_001", "door_001", "" });

            Assert.AreEqual(3, StaticCount());
            Assert.AreEqual(2, RemoveSolidsOf(arena, "door_001"));
            Assert.AreEqual(1, StaticCount());
            Assert.AreEqual(2, (int)Invoke(arena, arenaType, "GetRemovedSolidCount"),
                "the counter reads the two removed parts");
        }

        [Test]
        public void AFieldResetBringsTheSolidBackOnceWithItsOwnIdAndBake()
        {
            Component arena = NewArena(false, 0, "lantern_001", "");
            int idBefore = StaticSurfaceId(0);
            Vector3 centerBefore = StaticCenter(0);
            Assert.AreEqual(1, RemoveSolidsOf(arena, "lantern_001"));

            Assert.AreEqual(1, (int)Invoke(arena, arenaType, "RestoreRemovedSolids"));
            Assert.AreEqual(2, StaticCount());
            Assert.AreEqual(1, NearCountAt(new Vector3(3f, 0.5f, 0f)), "blocking again");
            Assert.IsTrue(HasStaticEntry(idBefore, centerBefore), "same surface id, same baked centre");

            Assert.AreEqual(0, (int)Invoke(arena, arenaType, "RestoreRemovedSolids"),
                "a second reset registers nothing twice");
            Assert.AreEqual(2, StaticCount());

            Assert.AreEqual(1, RemoveSolidsOf(arena, "lantern_001"), "and it can be rolled up again");
            Assert.AreEqual(1, StaticCount());
        }

        [Test]
        public void TwoHostsOnOneMotorNeverTouchEachOthersSolids()
        {
            // The hub's shape: one motor, the first host clears, the second appends with its ids
            // offset by what came before. Both hosts have a solid called "lantern_001".
            Component first = NewArena(true, 0, "lantern_001", "");
            Component second = NewArena(false, 2, "lantern_001", "");
            Assert.AreEqual(4, StaticCount());

            Assert.AreEqual(1, RemoveSolidsOf(first, "lantern_001"));
            Assert.AreEqual(3, StaticCount());
            Assert.AreEqual(0, (int)Invoke(second, arenaType, "GetRemovedSolidCount"),
                "the second host's record is untouched");
            Assert.IsTrue(HasStaticEntry(StaticIdOf(second, 0), BoxCenterOf(second, 0)),
                "the second host's twin is still in the motor");

            Assert.AreEqual(1, RemoveSolidsOf(second, "lantern_001"));
            Assert.AreEqual(2, StaticCount());

            Assert.AreEqual(1, (int)Invoke(first, arenaType, "RestoreRemovedSolids"));
            Assert.AreEqual(3, StaticCount(), "restoring the first host does not bring back the second's");
            Assert.AreEqual(1, (int)Invoke(second, arenaType, "RestoreRemovedSolids"));
            Assert.AreEqual(4, StaticCount());
        }

        [Test]
        public void AHostWhoseTableWasDroppedNeitherRemovesNorRestoresIntoTheNewLevel()
        {
            Component oldLevel = NewArena(true, 0, "lantern_001", "");
            Assert.AreEqual(1, RemoveSolidsOf(oldLevel, "lantern_001"));
            int epochBefore = (int)Invoke(motor, motorType, "GetStaticObbEpoch");

            // A level switch: the motor's tables are dropped, another level registers.
            Invoke(motor, motorType, "ClearObstacles");
            Assert.AreEqual(epochBefore + 1, (int)Invoke(motor, motorType, "GetStaticObbEpoch"));
            Component newLevel = NewArena(false, 0, "lantern_001", "");
            Assert.AreEqual(2, StaticCount());

            Assert.AreEqual(0, RemoveSolidsOf(oldLevel, "lantern_001"),
                "the old host's records describe a table that is gone");
            Assert.AreEqual(0, (int)Invoke(oldLevel, arenaType, "RestoreRemovedSolids"),
                "nothing is written into the new level's table");
            Assert.AreEqual(2, StaticCount(), "the new level's solids are intact and not doubled");

            // And the new level's own twin still works.
            Assert.AreEqual(1, RemoveSolidsOf(newLevel, "lantern_001"));
            Assert.AreEqual(1, StaticCount());
        }

        [Test]
        public void ARegisterAfterASwitchBakesEverythingAgainWithNothingRemoved()
        {
            Component arena = NewArena(true, 0, "lantern_001", "");
            Assert.AreEqual(1, RemoveSolidsOf(arena, "lantern_001"));
            Assert.AreEqual(1, StaticCount());

            // The switch back to this level: the switch host clears the motor and re-registers.
            Invoke(motor, motorType, "ClearObstacles");
            Invoke(arena, arenaType, "Register", true, 0, 0);

            Assert.AreEqual(2, StaticCount(), "every box is baked again, the removed twin included");
            Assert.AreEqual(0, (int)Invoke(arena, arenaType, "GetRemovedSolidCount"));
            Assert.AreEqual(0, (int)Invoke(arena, arenaType, "RestoreRemovedSolids"),
                "the field reset that follows registers nothing a second time");
            Assert.AreEqual(2, StaticCount());
        }

        // ---------------------------------------------------------------- helpers ---

        private BoxCollider Box(string name, Vector3 position)
        {
            GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            go.transform.SetParent(geometryRoot.transform, false);
            go.transform.position = position;
            go.transform.localScale = new Vector3(1f, 1f, 1f);
            Physics.SyncTransforms();
            return go.GetComponent<BoxCollider>();
        }

        // One arena host with a twin box at +3 m (id twinId) and a plain box at -3 m.
        // `idOffset` is the Register box offset a second host on the same motor passes.
        private Component NewArena(bool clearFirst, int idOffset, string twinId, string plainId)
        {
            return NewArenaFromBoxes(clearFirst, idOffset, new[]
            {
                Box("twin_" + made.Count, new Vector3(3f, 0.5f, 0f)),
                Box("plain_" + made.Count, new Vector3(-3f, 0.5f, 0f))
            }, new[] { twinId, plainId });
        }

        private Component NewArenaFromBoxes(bool clearFirst, int idOffset, BoxCollider[] boxes,
            string[] itemIds)
        {
            GameObject go = new GameObject("TwinSolidTestsArena" + made.Count);
            made.Add(go);
            Component arena = go.AddComponent(arenaType);
            SetField(arena, "motor", motor);
            SetField(arena, "staticBoxes", boxes);
            SetField(arena, "staticBoxItemIds", itemIds);
            SetField(arena, "rampBoxes", null);
            SetField(arena, "boundaryWalls", null);
            SetField(arena, "logSetupLine", false);
            SetField(arena, "autoBeginPlaying", false);
            Invoke(arena, arenaType, "Register", clearFirst, idOffset, 0);
            return arena;
        }

        private int RemoveSolidsOf(Component arena, string itemId)
        {
            return (int)Invoke(arena, arenaType, "RemoveSolidsOf", itemId);
        }

        private int StaticCount()
        {
            return (int)Invoke(motor, motorType, "GetStaticObbCount");
        }

        private int PropCount()
        {
            return (int)GetField(motor, motorType, "propObbCount");
        }

        private int StaticSurfaceId(int slot)
        {
            return ((int[])GetField(motor, motorType, "staticObbSurfaceId"))[slot];
        }

        private Vector3 StaticCenter(int slot)
        {
            return ((Vector3[])GetField(motor, motorType, "staticObbCenter"))[slot];
        }

        private bool HasStaticEntry(int surfaceId, Vector3 center)
        {
            int count = StaticCount();
            for (int i = 0; i < count; i++)
            {
                if (StaticSurfaceId(i) != surfaceId) continue;
                if ((StaticCenter(i) - center).sqrMagnitude < 1e-8f) return true;
            }
            return false;
        }

        private int StaticIdOf(Component arena, int box)
        {
            return ((int[])GetField(arena, arenaType, "boxBakeSurfaceId"))[box];
        }

        private Vector3 BoxCenterOf(Component arena, int box)
        {
            return ((Vector3[])GetField(arena, arenaType, "boxBakeCenter"))[box];
        }

        // The near window the ball sees at `center`: how many static solids are in reach.
        private int NearCountAt(Vector3 center)
        {
            SetFieldValue(motor, motorType, "ballCenterWorld", center);
            Invoke(motor, motorType, "FillNearWindow", 0.3f, 0f);
            return (int)Invoke(motor, motorType, "GetNearObbCount");
        }

        private bool SetPropBlock(Vector3[] centers)
        {
            int n = centers.Length;
            Vector3[] ax = new Vector3[n];
            Vector3[] ay = new Vector3[n];
            Vector3[] az = new Vector3[n];
            Vector3[] half = new Vector3[n];
            int[] ids = new int[n];
            int[] roles = new int[n];
            for (int i = 0; i < n; i++)
            {
                ax[i] = Vector3.right;
                ay[i] = Vector3.up;
                az[i] = Vector3.forward;
                half[i] = new Vector3(0.2f, 0.2f, 0.2f);
                ids[i] = 100 + i;
                roles[i] = 3;
            }
            return (bool)Invoke(motor, motorType, "SetPropObbs", centers, ax, ay, az, half, ids,
                roles, n);
        }

        private static void AssertVector(Vector3 expected, Vector3 actual)
        {
            Assert.AreEqual(0f, (expected - actual).magnitude, 1e-5f,
                "expected " + expected + " but was " + actual);
        }

        private static object GetField(Component target, Type type, string name)
        {
            FieldInfo field = type.GetField(name, Any);
            Assert.IsNotNull(field, name + " field not found");
            return field.GetValue(target);
        }

        private static void SetFieldValue(Component target, Type type, string name, object value)
        {
            FieldInfo field = type.GetField(name, Any);
            Assert.IsNotNull(field, name + " field not found");
            field.SetValue(target, value);
        }

        private static void SetField(Component target, string name, object value)
        {
            SetFieldValue(target, target.GetType(), name, value);
        }

        private static object Invoke(Component target, Type type, string name, params object[] args)
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
    }
}
