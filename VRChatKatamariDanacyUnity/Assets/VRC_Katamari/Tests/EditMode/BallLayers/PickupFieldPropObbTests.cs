using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace VRCKatamari.Tests
{
    /// <summary>
    /// Pins the pickup field's prop solids: one true OBB per selected blocking/support
    /// prop, built from <c>propLocalBounds</c> + the prop's world rotation
    /// (CONTACT_MODEL_V2 §4A.4/§5.2, S4b).
    /// </summary>
    /// <remarks>
    /// The motor is stepped through reflection exactly like <c>ContactModelV2Tests</c>. What
    /// is asserted is the handover: the host's own build AND the motor's prop block (count,
    /// surface id 100 + slot, role). The motor's block is read through its private arrays,
    /// because the only public readers are count/capacity getters.
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
            SetField(field, fieldType, "propGrowthMm3", new[] { 1000000L });
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
        public void TheHostSendsOnePropSolidToTheMotor()
        {
            ScanOnce();

            Assert.AreEqual(1, (int)Invoke(field, fieldType, "GetPropObbCount"),
                "one blocking prop is one solid");
            Assert.AreEqual(1, (int)Invoke(field, fieldType, "GetPropObbCandidateCount"));
            Assert.AreEqual(0, (int)Invoke(field, fieldType, "GetPropObbFallbackRows"),
                "a valid propLocalBounds row is used, so no row falls back");
            Assert.AreEqual(1, (int)Invoke(motor, motorType, "GetPropObbsCount"),
                "the host actually handed the solid to the motor's prop block");
            Assert.AreEqual(0, (int)Invoke(motor, motorType, "GetPropObbsRefusalCount"),
                "the block was accepted");

            // The handover convention: surface id 100 + slot, role 3 = blocking
            // (ContactCodes.RoleBlocking; role 2 = a support prop).
            int[] surfaceIds = GetArray<int>(motorType, "staticObbSurfaceId");
            int[] roles = GetArray<int>(motorType, "staticObbRole");
            Assert.AreEqual(100, surfaceIds[0], "prop surface id 100 + slot");
            Assert.AreEqual(3, roles[0], "a blocking prop is role 3");

            Assert.AreEqual(1, (int)Invoke(field, fieldType, "GetBlockingPropCount"));
            Assert.AreEqual(0, (int)Invoke(field, fieldType, "GetSupportPropCount"));
        }

        private void ScanOnce()
        {
            SetField(field, fieldType, "nextBlockingScanTime", 0f);
            Invoke(field, fieldType, "UpdatePropObstacles");
        }

        private T[] GetArray<T>(Type type, string fieldName)
        {
            FieldInfo fieldInfo = type.GetField(fieldName, Any);
            Assert.IsNotNull(fieldInfo, fieldName + " field not found");
            return (T[])fieldInfo.GetValue(motor);
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
