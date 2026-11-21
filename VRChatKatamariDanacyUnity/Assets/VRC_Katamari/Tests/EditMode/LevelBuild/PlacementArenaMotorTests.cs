using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace VRCKatamari.Tests
{
    /// <summary>
    /// Pins the importer's link between a level's <c>LevelArenaHost</c> and the ball motor
    /// (<c>PlacementImporter.WireArenaMotor</c> / <c>HandleSharedArenaHosts</c>).
    /// </summary>
    /// <remarks>
    /// The arena host registers only from its own <c>Update</c>, which returns silently while its
    /// <c>motor</c> is empty. The reference level RefWlkVs2 was built that way: 33 boxes were
    /// written to the host and none ever reached the motor, with no log line to say so. These
    /// tests pin the three answers: link the one motor; never guess when there are none or
    /// several; and switch off only a placeholder host (scene root, no geometry, reference scene),
    /// because several hosts on one motor are the normal shape of the hub and the test field.
    /// The importer lives in the editor assembly and UdonSharp in an assembly the tests do not
    /// reference, so both the importer and the two behaviours are reached by reflection, like the
    /// other importer and arena tests.
    /// </remarks>
    public class PlacementArenaMotorTests
    {
        private const string ImporterTypeName = "VRCKatamari.Editor.LevelBuild.PlacementImporter";
        private const string ReportTypeName = "VRCKatamari.Editor.LevelBuild.PlacementImportReport";

        private const string MotorTypeName = "VRCKatamari.BallMotor.KatamariBallMotor";
        private const string HostTypeName = "VRCKatamari.Story.LevelArenaHost";

        private Type importerType;
        private Type reportType;
        private Type motorType;
        private Type hostType;
        private object report;
        private readonly List<GameObject> made = new List<GameObject>();

        [SetUp]
        public void SetUp()
        {
            importerType = FindType(ImporterTypeName);
            reportType = FindType(ReportTypeName);
            Assert.IsNotNull(importerType, ImporterTypeName + " not loaded");
            Assert.IsNotNull(reportType, ReportTypeName + " not loaded");
            motorType = FindType(MotorTypeName);
            hostType = FindType(HostTypeName);
            Assert.IsNotNull(motorType, MotorTypeName + " not loaded");
            Assert.IsNotNull(hostType, HostTypeName + " not loaded");
            report = Activator.CreateInstance(reportType);
        }

        [TearDown]
        public void TearDown()
        {
            for (int i = 0; i < made.Count; i++)
            {
                if (made[i] != null) UnityEngine.Object.DestroyImmediate(made[i]);
            }
            made.Clear();
        }

        [Test]
        public void TheOnlyMotorIsLinkedIntoAnEmptyHost()
        {
            Component motor = NewMotor("Motor");
            Component host = NewHost("Level", null, null);
            var serialized = new SerializedObject(host);

            object result = WireArenaMotor(serialized.FindProperty("motor"), Motors(motor));
            serialized.ApplyModifiedPropertiesWithoutUndo();

            Assert.AreSame(motor, result);
            Assert.AreSame(motor, new SerializedObject(host).FindProperty("motor").objectReferenceValue,
                "the importer writes the link onto the host");
            Assert.AreEqual(1, WiringWrites(), "one scene wiring write is counted");
            Assert.IsTrue(HasNote("linked it to"), "the log says what was linked");
        }

        [Test]
        public void AnExistingLinkIsKeptAndSecondImportWritesNothing()
        {
            Component motor = NewMotor("Motor");
            Component other = NewMotor("OtherMotor");
            Component host = NewHost("Level", motor, null);
            var serialized = new SerializedObject(host);

            object result = WireArenaMotor(serialized.FindProperty("motor"), Motors(motor, other));

            Assert.AreSame(motor, result, "a hand-set link wins even when the scene has two motors");
            Assert.AreEqual(0, WiringWrites());
            Assert.AreEqual(0, NoteCount(), "an already-linked host is silent");
        }

        [Test]
        public void NoMotorLeavesTheHostAloneWithAWarning()
        {
            Component host = NewHost("Level", null, null);
            var serialized = new SerializedObject(host);

            object result = WireArenaMotor(serialized.FindProperty("motor"), Motors());

            Assert.IsNull(result);
            Assert.IsNull(serialized.FindProperty("motor").objectReferenceValue);
            Assert.AreEqual(0, WiringWrites());
            Assert.IsTrue(HasNote("warning"));
            Assert.IsTrue(HasNote("no KatamariBallMotor"));
        }

        [Test]
        public void SeveralMotorsAreNeverGuessed()
        {
            Component a = NewMotor("A");
            Component b = NewMotor("B");
            Component host = NewHost("Level", null, null);
            var serialized = new SerializedObject(host);

            object result = WireArenaMotor(serialized.FindProperty("motor"), Motors(a, b));

            Assert.IsNull(result);
            Assert.IsNull(serialized.FindProperty("motor").objectReferenceValue,
                "linking one of two motors would register the level with the wrong ball");
            Assert.IsTrue(HasNote("2 KatamariBallMotors"));
        }

        [Test]
        public void ARootPlaceholderHostInAReferenceSceneIsSwitchedOff()
        {
            Component motor = NewMotor("Motor");
            Component level = NewHost("Level", motor, null);
            Component template = NewHost("LevelArena", motor, null);

            int disabled = HandleShared(level, motor, Hosts(level, template), true);

            Assert.AreEqual(1, disabled);
            Assert.IsFalse(Enabled(template), "the template's empty host no longer overwrites the floor");
            Assert.IsTrue(Enabled(level), "the level's own host is never touched");
            Assert.IsTrue(HasNote("disabled the placeholder LevelArenaHost 'LevelArena'"),
                "the log names the host it disabled");
            Assert.AreEqual(1, WiringWrites());
        }

        [Test]
        public void ASharedHostOutsideAReferenceSceneOnlyGetsAWarning()
        {
            Component motor = NewMotor("Motor");
            Component level = NewHost("Level", motor, null);
            Component template = NewHost("LevelArena", motor, null);

            int disabled = HandleShared(level, motor, Hosts(level, template), false);

            Assert.AreEqual(0, disabled);
            Assert.IsTrue(Enabled(template), "the hub and the test field keep several hosts per motor");
            Assert.IsTrue(HasNote("share this motor"));
            Assert.IsTrue(HasNote("LevelArena"));
        }

        [Test]
        public void AHostThatRegistersGeometryIsNeverDisabled()
        {
            Component motor = NewMotor("Motor");
            Component level = NewHost("Level", motor, null);
            Component busy = NewHost("Busy", motor, null);
            BoxCollider box = new GameObject("Box").AddComponent<BoxCollider>();
            made.Add(box.gameObject);
            SetBoxes(busy, box);

            int disabled = HandleShared(level, motor, Hosts(level, busy), true);

            Assert.AreEqual(0, disabled);
            Assert.IsTrue(Enabled(busy));
            Assert.IsTrue(HasNote("share this motor"));
        }

        [Test]
        public void AChildHostIsNeverDisabled()
        {
            Component motor = NewMotor("Motor");
            Component level = NewHost("Level", motor, null);
            GameObject shell = new GameObject("IslandShell");
            made.Add(shell);
            Component inner = NewHost("InnerArena", motor, shell.transform);

            int disabled = HandleShared(level, motor, Hosts(level, inner), true);

            Assert.AreEqual(0, disabled, "an island's host belongs to the hub, not to this import");
            Assert.IsTrue(Enabled(inner));
        }

        [Test]
        public void AHostOnAnotherMotorIsIgnored()
        {
            Component motor = NewMotor("Motor");
            Component elsewhere = NewMotor("Elsewhere");
            Component level = NewHost("Level", motor, null);
            Component foreign = NewHost("Foreign", elsewhere, null);

            int disabled = HandleShared(level, motor, Hosts(level, foreign), true);

            Assert.AreEqual(0, disabled);
            Assert.IsTrue(Enabled(foreign));
            Assert.AreEqual(0, NoteCount(), "a host on a different motor is none of this import's business");
        }

        // ---------------------------------------------------------------- helpers ---

        private Component NewMotor(string name)
        {
            GameObject go = new GameObject(name);
            made.Add(go);
            return go.AddComponent(motorType);
        }

        private Array Motors(params Component[] motors)
        {
            Array array = Array.CreateInstance(motorType, motors.Length);
            for (int i = 0; i < motors.Length; i++) array.SetValue(motors[i], i);
            return array;
        }

        private Array Hosts(params Component[] hosts)
        {
            Array array = Array.CreateInstance(hostType, hosts.Length);
            for (int i = 0; i < hosts.Length; i++) array.SetValue(hosts[i], i);
            return array;
        }

        private static bool Enabled(Component behaviour)
        {
            return ((Behaviour)behaviour).enabled;
        }

        private Component NewHost(string name, Component motor, Transform parent)
        {
            GameObject go = new GameObject(name);
            made.Add(go);
            if (parent != null) go.transform.SetParent(parent, false);
            Component host = go.AddComponent(hostType);
            if (motor != null)
            {
                var serialized = new SerializedObject(host);
                serialized.FindProperty("motor").objectReferenceValue = motor;
                serialized.ApplyModifiedPropertiesWithoutUndo();
            }
            return host;
        }

        private static void SetBoxes(Component host, BoxCollider box)
        {
            var serialized = new SerializedObject(host);
            SerializedProperty boxes = serialized.FindProperty("staticBoxes");
            boxes.arraySize = 1;
            boxes.GetArrayElementAtIndex(0).objectReferenceValue = box;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        private object WireArenaMotor(SerializedProperty property, Array motors)
        {
            return Call("WireArenaMotor", property, motors, report);
        }

        private int HandleShared(Component host, Component motor, Array all,
            bool referenceScene)
        {
            return (int)Call("HandleSharedArenaHosts", host, motor, all, referenceScene, report);
        }

        private object Call(string name, params object[] args)
        {
            MethodInfo method = importerType.GetMethod(name,
                BindingFlags.Public | BindingFlags.Static);
            Assert.IsNotNull(method, "PlacementImporter." + name + " not found");
            try
            {
                return method.Invoke(null, args);
            }
            catch (TargetInvocationException exception)
            {
                throw exception.InnerException ?? exception;
            }
        }

        private int WiringWrites()
        {
            return (int)reportType.GetField("V2SceneWiringWrites").GetValue(report);
        }

        private List<string> Notes()
        {
            return (List<string>)reportType.GetField("Notes").GetValue(report);
        }

        private int NoteCount()
        {
            return Notes().Count;
        }

        private bool HasNote(string fragment)
        {
            List<string> notes = Notes();
            for (int i = 0; i < notes.Count; i++)
            {
                if (notes[i].Contains(fragment)) return true;
            }
            return false;
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
