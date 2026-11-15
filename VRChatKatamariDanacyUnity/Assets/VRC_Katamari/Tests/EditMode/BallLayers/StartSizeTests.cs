using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace VRCKatamari.Tests
{
    /// <summary>
    /// Pins the level-level start size (parity D6): a level's start diameter is authoritative
    /// and the baseline volume is derived from it in one place, while a level that does not set
    /// one keeps the calibration's 0.30 m start ball.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The defect this pins (F4, reference level run): <c>level.ballDiameterStart = 0.05</c> in
    /// the placement document reached only the importer's chord budget, because the motor had
    /// no start-size input at all. The ball started at
    /// <c>baselineVolumeUnits 3.4390244 * cubicMetersPerVolumeUnit 0.0041</c> = 0.0141 m^3,
    /// i.e. at 0.2997 m, which is six times the original first level's 5 cm ball.
    /// </para>
    /// <para>
    /// The numbers are hand calculations, not copies of the implementation: a sphere of
    /// diameter d has volume <c>pi/6 * d^3</c>, and one volume unit is 0.0041 m^3, so a 0.05 m
    /// ball is <c>pi/6 * 0.05^3 / 0.0041</c> = 0.015963 units. Continuous volume is NOT
    /// approved (PARITY_AUDIT 5.3), so the unit count stays an integer ledger unit and only the
    /// baseline (a volume) is derived.
    /// </para>
    /// </remarks>
    public class StartSizeTests
    {
        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic |
            BindingFlags.Public;

        private const float CubicMetersPerUnit = 0.0041f;
        private const float CalibrationStartDiameterM = 0.2997f;

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

            // The baseline is derived, not authored: one sphere of that diameter in the
            // ledger's volume unit. pi/6 * 0.05^3 = 6.544985e-5 m^3, / 0.0041 = 0.0159634.
            float baseline = (float)GetField("baselineVolumeUnits");
            Assert.AreEqual(0.0159634f, baseline, 1e-5f,
                "the baseline volume is the 0.05 m ball expressed in volume units");
            Assert.AreEqual(0.05f, DiameterOfBaseline(baseline), 0.001f,
                "the derived baseline maps back to the same diameter");
        }

        [Test]
        public void ALegacyLevelKeepsTheCalibrationStartBall()
        {
            // Nothing set: the scene predates the field, so its authored baseline volume is
            // the start size. This is the value the StoryMode run measured (0.2997369 m).
            Invoke("Start");

            Assert.AreEqual(0f, (float)GetField("startDiameterWorldM"));
            Assert.AreEqual(CalibrationStartDiameterM, (float)Call("GetSemanticDiameterWorldM"),
                0.001f, "a legacy level must not move");
            Assert.AreEqual(3.4390244f, (float)GetField("baselineVolumeUnits"), 1e-6f,
                "the authored baseline is untouched");
        }

        [Test]
        public void AStartDiameterTheCapCannotHoldKeepsTheAuthoredBaseline()
        {
            // 10 m start: the core radius would be 5 m, far above maxCoreRadiusWorldM (1.1).
            // Writing that baseline would make every derivation fail and the ball would stay
            // at 0 m, so the refusal keeps the calibrated start ball with a warning instead.
            SetField("startDiameterWorldM", 10f);
            Invoke("Start");

            Assert.AreEqual(3.4390244f, (float)GetField("baselineVolumeUnits"), 1e-6f,
                "the authored baseline survives a refused derivation");
            Assert.AreEqual(CalibrationStartDiameterM, (float)Call("GetSemanticDiameterWorldM"),
                0.001f, "the ball still has a usable start size");
        }

        [Test]
        public void TheDerivedBaselineScalesWithTheCubeOfTheDiameter()
        {
            // Two levels, one at 5 cm and one at 22.2 cm (the original's first and second
            // stars): the baseline volumes must differ by (22.2/5)^3 = 87.6, which is the
            // property the size model consumes. This pins "one derivation, no second scale".
            SetField("startDiameterWorldM", 0.05f);
            Invoke("Start");
            float smallBaseline = (float)GetField("baselineVolumeUnits");

            UnityEngine.Object.DestroyImmediate(motorHost);
            motorHost = new GameObject("StartSizeTestsMotor22");
            motor = motorHost.AddComponent(motorType);
            SetField("startDiameterWorldM", 0.222f);
            Invoke("Start");
            float largeBaseline = (float)GetField("baselineVolumeUnits");

            float expectedRatio = (0.222f / 0.05f) * (0.222f / 0.05f) * (0.222f / 0.05f);
            Assert.AreEqual(expectedRatio, largeBaseline / smallBaseline, expectedRatio * 0.001f,
                "baseline volume is a cube of the diameter, so the two levels' balls agree");
        }

        // ---- helpers -----------------------------------------------------------

        /// <summary>The sphere-volume inverse the size model consumes, from first principles.</summary>
        private static float DiameterOfBaseline(float baselineUnits)
        {
            float volume = baselineUnits * CubicMetersPerUnit;
            float radius = Mathf.Pow(volume * 3f / (4f * Mathf.PI), 1f / 3f);
            return 2f * radius;
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
