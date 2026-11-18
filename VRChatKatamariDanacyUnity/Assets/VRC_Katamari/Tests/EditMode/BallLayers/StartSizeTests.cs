using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace VRCKatamari.Tests
{
    /// <summary>
    /// Pins the level-level start size (parity D6) in the fixed-point model: a level's start
    /// diameter is authoritative and becomes <c>startVolumeMm3</c> in ONE conversion, while a
    /// level that does not set one keeps the serialized calibration start volume.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Steps C+D retired <c>baselineVolumeUnits</c> (3.4390244 units) and
    /// <c>cubicMetersPerVolumeUnit</c> (0.0041 m^3/unit). The start ball is now a fixed-point
    /// volume, and the level input is still a diameter (parity D6).
    /// </para>
    /// <para>
    /// Numbers are hand calculations: <c>V = pi/6 * d^3 * 1e9</c> mm^3, so 0.2997 m ->
    /// 14,094,798 mm^3, 0.05 m -> 65,450 mm^3 and 0.55 m -> 87,113,746 mm^3. The tests read
    /// the private field by reflection because the motor's UdonSharp fields are not public
    /// API; they assert the DERIVED volume, never a re-implementation of the formula.
    /// </para>
    /// </remarks>
    public class StartSizeTests
    {
        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic |
            BindingFlags.Public;

        private const long CalibrationStartMm3 = 14094798L;

        // Same float32 volume budget as SizeCalibrationTests (FIXED_POINT_STEP_CD section 4):
        // the start volume is computed with Mathf.Pow in float, so a double golden can differ
        // in the last bits by <= ~7e-7 relative; 2e-6 with a 3x margin.
        private const double FloatVolumeBudget = 2e-6;

        private static void AssertVolume(long expected, long actual)
        {
            Assert.IsTrue(Math.Abs((double)actual - (double)expected) <=
                Math.Abs((double)expected) * FloatVolumeBudget + 1.0,
                "start volume " + actual + " must equal " + expected +
                " within the float32 budget 2e-6 relative");
        }

        private Type motorType;
        private GameObject motorHost;
        private Component motor;

        [SetUp]
        public void SetUp()
        {
            motorType = FindType("VRCKatamari.BallMotor.KatamariBallMotor");
            Assert.IsNotNull(motorType, "KatamariBallMotor type not loaded");
            motorHost = new GameObject("StartSizeTestsMotor");
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
        public void ALevelThatAsksForFiveCentimetresStartsAtFiveCentimetres()
        {
            SetField("startDiameterWorldM", 0.05f);
            Invoke("Start");

            float diameter = (float)Call("GetSemanticDiameterWorldM");
            Assert.AreEqual(0.05f, diameter, 0.001f,
                "the level's start diameter is the ball's start diameter");

            // pi/6 * 0.05^3 * 1e9 = 65,449.8 -> 65,450 mm^3 (rounded once).
            long startMm3 = (long)GetField("startVolumeMm3");
            AssertVolume(65450L, startMm3);
            Assert.AreEqual(0.05f, DiameterOf(startMm3), 0.001f,
                "the derived start volume maps back to the same diameter");
        }

        [Test]
        public void ALegacyLevelKeepsTheCalibrationStartVolume()
        {
            // startDiameterWorldM = 0 (unset): the serialized start volume is the start ball.
            Assert.AreEqual(0L, (float)GetField("startDiameterWorldM"), 0f);
            Assert.AreEqual(CalibrationStartMm3, (long)GetField("startVolumeMm3"));
            Invoke("Start");
            Assert.AreEqual(0.2997f, (float)Call("GetSemanticDiameterWorldM"), 0.001f);

            // And it says so in the ready line's startSize marker.
            Assert.AreEqual(CalibrationStartMm3, (long)GetField("startVolumeMm3"),
                "a scene with no level start keeps the authored start volume");
        }

        [Test]
        public void AStartDiameterAboveTheGlobalCapIsRefusedAndTheStartKept()
        {
            SetField("startDiameterWorldM", 401f);   // above the global cap
            Invoke("Start");

            Assert.AreEqual(CalibrationStartMm3, (long)GetField("startVolumeMm3"),
                "an over-cap start must not be written: the ball would start at the cap");
            Assert.AreEqual(0.2997f, (float)Call("GetSemanticDiameterWorldM"), 0.001f);
        }

        [Test]
        public void TheStartVolumeIsRecomputedOnEveryStartNotAccumulated()
        {
            SetField("startDiameterWorldM", 0.55f);
            Invoke("Start");
            long first = (long)GetField("startVolumeMm3");
            AssertVolume(87113746L, first);

            // Start again with a different level value: the conversion is a pure function of
            // the current field, so the second number must not be first * something.
            SetField("startDiameterWorldM", 0.30f);
            Invoke("Start");
            long second = (long)GetField("startVolumeMm3");
            // The file's float32 budget (AssertVolume, 2e-6 relative) is the right comparison
            // here too: 0.30 m through Mathf.Pow is 14,137,169 in float (the exact double value
            // is 14,137,167), a 1.4e-7 relative difference - inside the documented budget, not
            // a wrong conversion.
            AssertVolume(14137167L, second);
            Assert.AreNotEqual(first, second);
        }

        /// <summary>The equivalent-sphere diameter in metres for a total volume in mm^3.</summary>
        private static float DiameterOf(long volumeMm3)
        {
            return (float)Math.Pow(6.0 * volumeMm3 / 1e9 / Math.PI, 1.0 / 3.0);
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

        private object Call(string name, params object[] args)
        {
            MethodInfo method = motorType.GetMethod(name, Any);
            Assert.IsNotNull(method, name + " not found on KatamariBallMotor");
            return method.Invoke(motor, args);
        }

        private object Invoke(string name, params object[] args)
        {
            return Call(name, args);
        }

        private object GetField(string name)
        {
            FieldInfo fieldInfo = motorType.GetField(name, Any);
            Assert.IsNotNull(fieldInfo, name + " field not found on KatamariBallMotor");
            return fieldInfo.GetValue(motor);
        }

        private void SetField(string name, object value)
        {
            FieldInfo fieldInfo = motorType.GetField(name, Any);
            Assert.IsNotNull(fieldInfo, name + " field not found on KatamariBallMotor");
            fieldInfo.SetValue(motor, value);
        }
    }
}
