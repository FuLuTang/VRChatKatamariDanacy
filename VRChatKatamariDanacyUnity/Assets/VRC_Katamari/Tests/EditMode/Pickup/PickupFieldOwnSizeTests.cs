using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace VRCKatamari.Tests
{
    /// <summary>
    /// Pins the pickup field's size source: the absorb gate, the blocking predicate and the
    /// growth fallback read the prop's OWN size (<c>propLocalBounds</c>), and a scene that
    /// has no usable column falls back to <c>propBounds</c> visibly, not silently.
    /// </summary>
    /// <remarks>
    /// The fixture is one prop turned 45 degrees whose own size is a 0.10 m cube (Req 237
    /// mm at the default k) but whose stand-in world box is 0.6 x 2 x 0.6 m (Req about
    /// 2,130 mm, and tall enough to be a wall rather than a step). The motor's start ball
    /// is 299 mm, so the prop is food by its own size and a wall by its world box; the
    /// scan's blocking count tells which one the host read.
    /// </remarks>
    public class PickupFieldOwnSizeTests
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

            motorHost = new GameObject("PickupFieldOwnSizeTestsMotor");
            motor = motorHost.AddComponent(motorType);
            Invoke(motor, motorType, "Start");

            propObject = new GameObject("turned_prop");
            propObject.transform.position = new Vector3(0.4f, 0.4f, 0f);
            propObject.transform.rotation = Quaternion.AngleAxis(45f, Vector3.up);

            fieldHost = new GameObject("PickupFieldOwnSizeTestsField");
            field = fieldHost.AddComponent(fieldType);
            SetField(field, fieldType, "motor", motor);
            SetField(field, fieldType, "propTransforms", new[] { propObject.transform });
            SetField(field, fieldType, "itemIds", new[] { "turned_prop" });
            SetField(field, fieldType, "kinds", new[] { "furniture" });
            SetField(field, fieldType, "propBounds", new[] { new Vector3(0.6f, 2f, 0.6f) });
            SetField(field, fieldType, "propGrowthMm3", new[] { 1000000L });
        }

        [TearDown]
        public void TearDown()
        {
            if (fieldHost != null) UnityEngine.Object.DestroyImmediate(fieldHost);
            if (propObject != null) UnityEngine.Object.DestroyImmediate(propObject);
            if (motorHost != null) UnityEngine.Object.DestroyImmediate(motorHost);
        }

        [Test]
        public void ATurnedPropIsPricedByItsOwnSizeSoItDoesNotBlock()
        {
            SetField(field, fieldType, "propLocalBounds",
                new[] { new Vector3(0.1f, 0.1f, 0.1f) });
            Invoke(field, fieldType, "Start");

            Assert.AreEqual(0, (int)Invoke(field, fieldType, "GetOwnSizeFallbackRows"));
            Vector3 size = (Vector3)Invoke(field, fieldType, "GetPropOwnSize", 0);
            Assert.AreEqual(0.1f, size.x, 1e-6f);
            Assert.AreEqual(0.1f, size.z, 1e-6f);

            ScanOnce();
            Assert.AreEqual(0, (int)Invoke(field, fieldType, "GetBlockingPropCount"),
                "a 237 mm requirement is food for the 299 mm start ball, however it is turned");
        }

        [Test]
        public void AMissingLocalColumnFallsBackToTheWorldBoxAndSaysSo()
        {
            Invoke(field, fieldType, "Start");

            Assert.AreEqual(1, (int)Invoke(field, fieldType, "GetOwnSizeFallbackRows"));
            Vector3 size = (Vector3)Invoke(field, fieldType, "GetPropOwnSize", 0);
            Assert.AreEqual(0.6f, size.x, 1e-6f, "the fallback is the world box");

            ScanOnce();
            Assert.AreEqual(1, (int)Invoke(field, fieldType, "GetBlockingPropCount"),
                "the old behaviour for an un-synced scene: priced by the world box");
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
