using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using VRCKatamari.Pickup;

namespace VRCKatamari.Tests
{
    /// <summary>
    /// Pins the ownership rule for the sets the pickup field writes into the motor: the first
    /// writer allocates, Start only ensures, and a buffer that is missing is a refused call
    /// with a reason instead of a null reference.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The defect this pins (F1, reference level run): the field's Update pushed a blocking
    /// set into a motor whose <c>Start</c> had never run, so the motor's blocking buffers were
    /// still null. The write threw <c>Object reference not set</c> inside
    /// <c>SetBlockingBoxes</c>, Udon halted that behaviour, and the ball never started - the
    /// whole level was unmeasurable. Unity does not order Start across GameObjects (and the
    /// motor's own step can derive a size before its Start event arrives), so neither order
    /// may be a crash.
    /// </para>
    /// <para>
    /// Reflection, like the other BallLayers/Pickup tests: this assembly references only
    /// <c>VRCKatamari.Runtime</c>, and the behaviours are UdonSharp behaviours that must be
    /// constructed the way the tests already construct them (<c>AddComponent</c> then an
    /// explicit <c>Start</c> invocation, because EditMode does not call Start).
    /// </para>
    /// </remarks>
    public class PickupMotorSetOwnershipTests
    {
        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic |
            BindingFlags.Public;

        // The fixture prop's gameplay volume (metres). A prop is a WALL only while it is
        // beyond the ball's absorb volume limit; below the limit it is food and the host is
        // right to register nothing for it. See BuildFieldWithOneBlockingProp.
        private static readonly Vector3 FixturePropBounds = new Vector3(0.3f, 0.3f, 0.3f);

        private Type motorType;
        private Type fieldType;
        private GameObject motorHost;
        private Component motor;
        private GameObject fieldHost;
        private Component field;
        private GameObject propObject;

        [SetUp]
        public void SetUp()
        {
            motorType = FindType("VRCKatamari.BallMotor.KatamariBallMotor");
            fieldType = FindType("VRCKatamari.Pickup.PickupFieldHost");
            Assert.IsNotNull(motorType, "KatamariBallMotor type not loaded");
            Assert.IsNotNull(fieldType, "PickupFieldHost type not loaded");

            // No Start here on purpose: which order the two behaviours start in is what the
            // tests below vary. A test that needs a started motor calls Start itself.
            motorHost = new GameObject("PickupMotorSetOwnershipTestsMotor");
            motor = motorHost.AddComponent(motorType);
            Assert.IsNotNull(motor, "AddComponent(KatamariBallMotor) returned null");
        }

        [TearDown]
        public void TearDown()
        {
            if (fieldHost != null) UnityEngine.Object.DestroyImmediate(fieldHost);
            if (propObject != null) UnityEngine.Object.DestroyImmediate(propObject);
            if (motorHost != null) UnityEngine.Object.DestroyImmediate(motorHost);
            fieldHost = null;
            propObject = null;
            motorHost = null;
            motor = null;
            field = null;
        }

        // ---- the motor's own contract ------------------------------------------

        [Test]
        public void ABlockingSetThatArrivesBeforeStartIsKept()
        {
            Vector3[] minima = { new Vector3(-0.05f, -0.05f, -0.05f) };
            Vector3[] maxima = { new Vector3(0.05f, 0.05f, 0.05f) };

            bool accepted = (bool)Call(motorType, motor, "SetBlockingBoxes", minima, maxima, 1);
            Assert.IsTrue(accepted, "a set that arrives before Start must be accepted");
            Assert.AreEqual(1, (int)Call(motorType, motor, "GetBlockingBoxCount"));
            Assert.AreEqual(0, (int)Call(motorType, motor, "GetBlockingBoxRefusalCount"),
                "nothing about the early arrival is a refusal");

            Array buffer = (Array)GetField(motorType, motor, "blockingMinimumBuffer");
            Assert.IsNotNull(buffer, "the entry point allocated the destination");
            Assert.AreEqual(6, buffer.Length, "the capacity is the motor's own constant");

            Invoke(motor, motorType, "Start");

            Assert.AreEqual(1, (int)Call(motorType, motor, "GetBlockingBoxCount"),
                "Start must keep the set that was registered before it, not re-allocate it away");
            Assert.Greater((float)Call(motorType, motor, "GetSemanticDiameterWorldM"), 0.29f,
                "Start still derives the ball's size (the set did not disturb it)");
        }

        [Test]
        public void ASupportSetThatArrivesBeforeStartIsKept()
        {
            Vector3[] minima = { new Vector3(-0.1f, 0f, -0.1f) };
            Vector3[] maxima = { new Vector3(0.1f, 0.02f, 0.1f) };

            bool accepted = (bool)Call(motorType, motor, "SetSupportProps", minima, maxima, 1);
            Assert.IsTrue(accepted, "a support set that arrives before Start must be accepted");
            Assert.AreEqual(1, (int)Call(motorType, motor, "GetSupportPropCount"));
            Assert.AreEqual(0, (int)Call(motorType, motor, "GetSupportPropRefusalCount"));

            Invoke(motor, motorType, "Start");
            Assert.AreEqual(1, (int)Call(motorType, motor, "GetSupportPropCount"),
                "Start must keep the support set it did not allocate");
        }

        [Test]
        public void ACandidateSubmittedBeforeStartIsQueued()
        {
            bool queued = (bool)Call(motorType, motor, "SubmitCandidate", true, "small",
                new Vector3(0.1f, 0.1f, 0.1f), new Vector3(0f, 0.05f, 0.2f), 3f, "early_prop", 0);
            Assert.IsTrue(queued, "the queue allocates on first use");
            Assert.AreEqual(1, (int)Call(motorType, motor, "GetPendingCandidateCount"));

            Invoke(motor, motorType, "Start");
            Assert.AreEqual(1, (int)Call(motorType, motor, "GetPendingCandidateCount"),
                "Start must not drop a candidate that was already queued");
        }

        [Test]
        public void ANullOrUndersizedBufferIsARefusedCallWithAReason()
        {
            Invoke(motor, motorType, "Start");
            Vector3[] minima = { new Vector3(-0.05f, -0.05f, -0.05f) };
            Vector3[] maxima = { new Vector3(0.05f, 0.05f, 0.05f) };

            // Null: the exact state F1 found the motor in.
            SetField(motorType, motor, "blockingMinimumBuffer", null);
            bool accepted = (bool)Call(motorType, motor, "SetBlockingBoxes", minima, maxima, 1);
            Assert.IsFalse(accepted, "a null destination is refused, not dereferenced");
            Assert.AreEqual("set buffers unavailable",
                (string)Call(motorType, motor, "GetBlockingBoxRefusalReason"));
            Assert.AreEqual(0, (int)Call(motorType, motor, "GetBlockingBoxCount"),
                "a refused call changes nothing");

            // Undersized: the same refusal, because the capacity constant is the contract.
            // (The blocking buffer is put back first, so the second refusal is about the
            // support buffer's LENGTH and not about the damage above.)
            SetField(motorType, motor, "blockingMinimumBuffer", new Vector3[6]);
            SetField(motorType, motor, "supportPropMaximumBuffer", new Vector3[1]);
            bool supportAccepted = (bool)Call(motorType, motor, "SetSupportProps",
                minima, maxima, 1);
            Assert.IsFalse(supportAccepted, "a shorter destination is refused");
            Assert.AreEqual("set buffers unavailable",
                (string)Call(motorType, motor, "GetSupportPropRefusalReason"));
        }

        // ---- the host's half: the F1 state, end to end -------------------------

        [Test]
        public void AHostPushWithNoStartIsAppliedInsteadOfThrowing()
        {
            // The F1 state: the motor has derived a size (its own step can do that before the
            // Start event arrives) while the sets' buffers do not exist yet. Anything the
            // field pushes here used to throw inside SetBlockingBoxes and halt the motor.
            SetField(motorType, motor, "semanticDiameterWorldM", 0.2997f);
            BuildFieldWithOneBlockingProp();
            AssertFixturePropIsAWall();

            Invoke(field, fieldType, "Update");

            Assert.AreEqual(1, (int)Call(motorType, motor, "GetBlockingBoxCount"),
                "the host's one blocking prop reached the motor");
            Assert.AreEqual(0, (int)Call(motorType, motor, "GetBlockingBoxRefusalCount"));

            // And the Start that arrives afterwards keeps it (the second half of "both
            // orders"): a re-allocation here would silently empty the ball's world.
            Invoke(motor, motorType, "Start");
            Assert.AreEqual(1, (int)Call(motorType, motor, "GetBlockingBoxCount"),
                "Start after the push keeps the set");
        }

        [Test]
        public void AHostScanBeforeTheMotorHasASizeWritesNothing()
        {
            // Start order the other way round: the field's Start and Update run first. The
            // motor reports no size yet, so the host waits instead of pricing a zero-diameter
            // ball at the world origin.
            BuildFieldWithOneBlockingProp();
            Assert.AreEqual(0f, (float)Call(motorType, motor, "GetSemanticDiameterWorldM"));

            Invoke(field, fieldType, "Update");

            Assert.AreEqual(0, (int)Call(motorType, motor, "GetBlockingBoxCount"),
                "nothing is registered against a ball whose size does not exist yet");
            Assert.AreEqual(0, (int)Call(motorType, motor, "GetSupportPropCount"));

            // Waiting, not skipping for ever: once the size exists the same scan registers.
            Invoke(motor, motorType, "Start");
            AssertFixturePropIsAWall();
            SetField(fieldType, field, "nextBlockingScanTime", 0f);
            Invoke(field, fieldType, "Update");
            Assert.Greater(
                (int)Call(motorType, motor, "GetBlockingBoxCount") +
                (int)Call(motorType, motor, "GetSupportPropCount"), 0,
                "the next scan after the motor's Start registers the prop");
        }

        // ---- fixtures ----------------------------------------------------------

        private void BuildFieldWithOneBlockingProp()
        {
            // One prop above the ball's bottom and within the scan margin that the motor
            // cannot absorb yet: a wall, not food. Both of the design's predicates are
            // satisfied by size alone (TERRAIN_SUPPORT table 4: beyond the absorb volume
            // limit AND rise > stepLimit):
            //   - volume: 0.30^3 = 0.027 m3 against a limit of (pi/6)*0.2997^3/10 =
            //     0.00141 m3, ~19x over, so its own D_abs is ~0.80 m and a 0.30 m ball never
            //     eats it. A 0.1 m cube (0.001 m3) would be BELOW that limit - absorbable,
            //     i.e. food - and the host would correctly register no obstacle for it;
            //   - rise: the top is 0.05 + 0.15 = 0.20 m above the ball's bottom, past every
            //     step limit this ball derives (0.25 * 0.2997 = 0.075), so it is blocking
            //     rather than a support prop.
            // AssertFixturePropIsAWall re-checks the volume half against the motor's own
            // limit in each test, so this fixture cannot quietly turn back into food.
            propObject = new GameObject("blocking_prop");
            propObject.transform.position = new Vector3(0.1f, 0.05f, 0f);
            propObject.AddComponent<MeshRenderer>();

            fieldHost = new GameObject("PickupMotorSetOwnershipTestsField");
            field = fieldHost.AddComponent(fieldType);
            SetField(fieldType, field, "motor", motor);
            SetField(fieldType, field, "propTransforms", new[] { propObject.transform });
            SetField(fieldType, field, "itemIds", new[] { "blocking_prop" });
            SetField(fieldType, field, "kinds", new[] { "furniture" });
            SetField(fieldType, field, "propBounds", new[] { FixturePropBounds });
            SetField(fieldType, field, "propLocalBounds", new[] { FixturePropBounds });
            SetField(fieldType, field, "valueUnits", new[] { 1L });
            Invoke(field, fieldType, "Start");
        }

        /// <summary>
        /// Asserts the fixture prop really is a wall under the gate's own definition, using the
        /// motor's own limit. Only callable once the motor has a size (its limit is 0 before).
        /// </summary>
        private void AssertFixturePropIsAWall()
        {
            float limit = (float)Call(motorType, motor, "GetAbsorbVolumeLimitWorldM3");
            Assert.Greater(limit, 0f, "the motor must have a size, so its absorb limit is priced");
            Assert.IsTrue(PickupAbsorbMath.IsBeyondAbsorbVolumeLimit(FixturePropBounds, limit),
                "the fixture prop must be beyond the ball's absorb volume limit; an absorbable "
                + "prop is food, and the host is right to register nothing for it");
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

        private static object Invoke(object target, Type type, string name, params object[] args)
        {
            return Call(type, target, name, args);
        }

        private static object GetField(Type type, object target, string name)
        {
            FieldInfo fieldInfo = type.GetField(name, Any);
            Assert.IsNotNull(fieldInfo, name + " field not found on " + type.Name);
            return fieldInfo.GetValue(target);
        }

        private static void SetField(Type type, object target, string name, object value)
        {
            FieldInfo fieldInfo = type.GetField(name, Any);
            Assert.IsNotNull(fieldInfo, name + " field not found on " + type.Name);
            fieldInfo.SetValue(target, value);
        }
    }
}
