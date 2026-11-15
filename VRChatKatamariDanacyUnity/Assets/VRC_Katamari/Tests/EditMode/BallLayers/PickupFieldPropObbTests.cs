using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace VRCKatamari.Tests
{
    /// <summary>
    /// Pins the pickup field's contact-v2 side: one true OBB per selected blocking/support
    /// prop while the motor reports v2, built from <c>propLocalBounds</c> + the prop's world
    /// rotation, and nothing at all while it reports v1 (CONTACT_MODEL_V2 §4A.4/§5.2, S4b).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The v1 sets (<c>SetBlockingBoxes</c>/<c>SetSupportProps</c>) are asserted to keep
    /// working too: they are the rollback path and they are fed from the same scan, so a
    /// broken gather that also empties the v1 tables would otherwise pass unnoticed.
    /// </para>
    /// <para>
    /// The motor is stepped through reflection exactly like <c>ContactModelV2Tests</c>. The
    /// prop solids are NOT registered with the motor yet (there is no dynamic OBB table - see
    /// the §7.6 request), so what is asserted here is the host's own answer, which is the half
    /// that exists today.
    /// </para>
    /// </remarks>
    public class PickupFieldPropObbTests
    {
        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic |
            BindingFlags.Public;

        private GameObject motorHost;
        private GameObject fieldHost;
        private GameObject propObject;
        private Component motor;
        private Component field;
        private Type motorType;
        private Type fieldType;

        [SetUp]
        public void SetUp()
        {
            motorType = FindType("VRCKatamari.BallMotor.KatamariBallMotor");
            fieldType = FindType("VRCKatamari.Pickup.PickupFieldHost");
            Assert.IsNotNull(motorType, "KatamariBallMotor type not loaded");
            Assert.IsNotNull(fieldType, "PickupFieldHost type not loaded");

            motorHost = new GameObject("PickupFieldPropObbTestsMotor");
            motor = motorHost.AddComponent(motorType);
            Invoke(motor, motorType, "Start");

            // One big absorb-proof prop at the ball's side: a 2 m cube whose top is far above
            // the step limit, so the scan classifies it as blocking.
            propObject = new GameObject("big_prop");
            propObject.transform.position = new Vector3(0.4f, 0.4f, 0f);
            propObject.transform.rotation = Quaternion.AngleAxis(25f, Vector3.up);

            fieldHost = new GameObject("PickupFieldPropObbTestsField");
            field = fieldHost.AddComponent(fieldType);
            SetField(field, fieldType, "motor", motor);
            SetField(field, fieldType, "propTransforms", new[] { propObject.transform });
            SetField(field, fieldType, "itemIds", new[] { "prop_big" });
            SetField(field, fieldType, "kinds", new[] { "furniture" });
            SetField(field, fieldType, "propBounds", new[] { new Vector3(2f, 2f, 2f) });
            SetField(field, fieldType, "propLocalBounds", new[] { new Vector3(2f, 2f, 2f) });
            SetField(field, fieldType, "valueUnits", new[] { 1 });
            Invoke(field, fieldType, "Start");
        }

        [TearDown]
        public void TearDown()
        {
            if (fieldHost != null) UnityEngine.Object.DestroyImmediate(fieldHost);
            if (propObject != null) UnityEngine.Object.DestroyImmediate(propObject);
            if (motorHost != null) UnityEngine.Object.DestroyImmediate(motorHost);
        }

        [Test]
        public void V2BuildsOnePropSolidAndKeepsTheV1Sets()
        {
            Invoke(motor, motorType, "SetContactModelV2Enabled", true);
            ScanOnce();

            Assert.AreEqual(1, (int)Invoke(field, fieldType, "GetPropObbCount"),
                "one blocking prop is one v2 solid");
            Assert.AreEqual(1, (int)Invoke(field, fieldType, "GetPropObbCandidateCount"),
                "the candidate flag comes from the same classifier as the v1 sets");
            Assert.AreEqual(0, (int)Invoke(field, fieldType, "GetPropObbFallbackRows"),
                "a valid propLocalBounds row is used, so no row falls back");
            Assert.AreEqual(1, (int)Invoke(field, fieldType, "GetBlockingPropCount"),
                "the v1 blocking set is still registered (the rollback path)");
            Assert.AreEqual(1, (int)Invoke(motor, motorType, "GetBlockingBoxCount"),
                "the motor accepted the v1 blocking box");
        }

        [Test]
        public void V1HasNoPropSolidAtAll()
        {
            // The flag is off: the host must not keep a stale v2 set around, because the v1
            // tables are the whole world in that mode.
            Invoke(motor, motorType, "SetContactModelV2Enabled", true);
            ScanOnce();
            Assert.AreEqual(1, (int)Invoke(field, fieldType, "GetPropObbCount"));

            Invoke(motor, motorType, "SetContactModelV2Enabled", false);
            ScanOnce();

            Assert.AreEqual(0, (int)Invoke(field, fieldType, "GetPropObbCount"),
                "a v1 motor clears the prop solids");
            Assert.AreEqual(1, (int)Invoke(field, fieldType, "GetBlockingPropCount"),
                "and the v1 blocking set keeps blocking");
        }

        private void ScanOnce()
        {
            SetField(field, fieldType, "nextBlockingScanTime", 0f);
            Invoke(field, fieldType, "UpdatePropObstacles");
        }

        private void SetField(Component target, Type type, string name, object value)
        {
            FieldInfo fieldInfo = type.GetField(name, Any);
            Assert.IsNotNull(fieldInfo, name + " field not found");
            fieldInfo.SetValue(target, value);
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
    }
}
