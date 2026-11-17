using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using VRCKatamari.Pickup;

namespace VRCKatamari.Tests
{
    /// <summary>
    /// Pins the ownership rule for the candidate queue the pickup field writes into the
    /// motor: the first writer allocates and Start only ensures, so a candidate submitted
    /// before the motor's Start is queued instead of throwing.
    /// </summary>
    /// <remarks>
    /// Reflection, like the other BallLayers/Pickup tests: this assembly references only
    /// <c>VRCKatamari.Runtime</c>, and the behaviours are UdonSharp behaviours that must be
    /// constructed the way the tests already construct them (<c>AddComponent</c> then an
    /// explicit <c>Start</c> invocation, because EditMode does not call Start).
    /// </remarks>
    public class PickupMotorSetOwnershipTests
    {
        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic |
            BindingFlags.Public;

        private Type motorType;
        private GameObject motorHost;
        private Component motor;

        [SetUp]
        public void SetUp()
        {
            motorType = FindType("VRCKatamari.BallMotor.KatamariBallMotor");
            Assert.IsNotNull(motorType, "KatamariBallMotor type not loaded");

            // No Start here on purpose: the test calls Start itself after the early submit.
            motorHost = new GameObject("PickupMotorSetOwnershipTestsMotor");
            motor = motorHost.AddComponent(motorType);
            Assert.IsNotNull(motor, "AddComponent(KatamariBallMotor) returned null");
        }

        [TearDown]
        public void TearDown()
        {
            if (motorHost != null) UnityEngine.Object.DestroyImmediate(motorHost);
            motorHost = null;
            motor = null;
        }

        [Test]
        public void ACandidateSubmittedBeforeStartIsQueued()
        {
            bool queued = (bool)Call(motorType, motor, "SubmitCandidate", true, "small",
                new Vector3(0.1f, 0.1f, 0.1f), new Vector3(0f, 0.05f, 0.2f), 3f, "early_prop", 0);
            Assert.IsTrue(queued, "the queue allocates on first use");
            Assert.AreEqual(1, (int)Call(motorType, motor, "GetPendingCandidateCount"));

            Call(motorType, motor, "Start");
            Assert.AreEqual(1, (int)Call(motorType, motor, "GetPendingCandidateCount"),
                "Start must not drop a candidate that was already queued");
        }

        private static Type FindType(string fullName)
        {
            Type direct = Type.GetType(fullName + ", VRCKatamari.Runtime");
            if (direct != null) return direct;
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type found = assembly.GetType(fullName);
                if (found != null) return found;
            }

            return null;
        }

        private static object Call(Type type, object target, string name, params object[] args)
        {
            MethodInfo method = type.GetMethod(name, Any);
            Assert.IsNotNull(method, name + " not found on " + type.Name);
            return method.Invoke(target, args);
        }
    }
}
