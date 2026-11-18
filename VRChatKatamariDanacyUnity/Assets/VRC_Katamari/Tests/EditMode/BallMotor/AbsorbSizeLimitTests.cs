using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using VRCKatamari.Pickup;

namespace VRCKatamari.Tests
{
    /// <summary>
    /// Pins the motor side of the ORIGINAL absorb gate with the fixed-point size model
    /// (Docs/OPEN_DECISIONS.md (r) C+D): the ball's diameter floors to whole millimetres, the
    /// retired per-config ratio/ceiling fields stay gone, and the sanity bound is derived from
    /// the ONE global cap.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Since steps C+D the motor's size inputs are <c>startVolumeMm3</c> and
    /// <c>maxBallDiameterM</c>; the retired fields this file guards are
    /// <c>maxItemVolumeWorldM3</c>, <c>absorbVolumeRatio</c> and the per-level
    /// <c>maxCoreRadiusWorldM</c>. Numbers are hand calculations from the original's formula
    /// (<c>Req = floor(1000*cbrt(60*k*a*b*c/pi))</c>) and the sphere relation, never copied
    /// from the implementation.
    /// </para>
    /// </remarks>
    public class AbsorbSizeLimitTests
    {
        private const string MotorTypeName =
            "VRCKatamari.BallMotor.KatamariBallMotor, VRCKatamari.Runtime";

        // pi/6 * 0.2997^3 * 1e9 mm^3, the serialized start volume.
        private const long StartMm3 = 14094798L;

        private GameObject host;
        private Component motor;
        private Type motorType;

        [SetUp]
        public void SetUp()
        {
            motorType = Type.GetType(MotorTypeName);
            if (motorType == null)
            {
                foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    motorType = assembly.GetType("VRCKatamari.BallMotor.KatamariBallMotor");
                    if (motorType != null) break;
                }
            }

            Assert.IsNotNull(motorType, "KatamariBallMotor type not loaded");
            host = new GameObject("AbsorbSizeLimitTestsMotor");
            motor = host.AddComponent(motorType);
            Assert.IsNotNull(motor, "AddComponent(KatamariBallMotor) returned null");
            Invoke("Start");
        }

        [TearDown]
        public void TearDown()
        {
            if (host != null) UnityEngine.Object.DestroyImmediate(host);
            host = null;
            motor = null;
        }

        [Test]
        public void TheRetiredSizeFieldsAreGone()
        {
            // A scene or prefab may still serialize these names; Unity ignores dead keys. A
            // reintroduced FIELD would let a config silently stop being the original's gate.
            Assert.IsNull(FieldOrNull("maxItemVolumeWorldM3"),
                "the 512 m^3 item ceiling must stay retired");
            Assert.IsNull(FieldOrNull("absorbVolumeRatio"),
                "the serialized ratio must stay retired (PickupAbsorbMath.AbsorbVolumeRatio)");
            Assert.IsNull(FieldOrNull("maxCoreRadiusWorldM"),
                "the per-level 1.1 m core cap is replaced by the global maxBallDiameterM");
            Assert.IsNull(FieldOrNull("baselineVolumeUnits"),
                "the integer-unit baseline is retired (step C)");
            Assert.IsNull(FieldOrNull("cubicMetersPerVolumeUnit"),
                "the per-unit volume constant is retired (step C)");
            Assert.IsNull(motorType.GetMethod("GetAbsorbVolumeLimitWorldM3",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public),
                "the per-step volume limit is not the gate's quantity any more");
        }

        [Test]
        public void TheStartBallIsAWholeMillimetreDiameter()
        {
            float diameter = Getter("GetSemanticDiameterWorldM");
            float ballMm = Getter("GetAbsorbGateDiameterMm");
            Assert.AreEqual(0.2997f, diameter, 0.005f);
            // floor(1000 * 0.2997 + 1e-3) = 300 for the exact 0.2997 sphere; the stored start
            // volume rounds to 0.2997 m, so the reading is 299 or 300 depending on the last
            // float bit - assert the FLOOR, not a guessed integer.
            Assert.AreEqual(Mathf.Floor(diameter * 1000f + 1e-3f), ballMm, 1e-6f);

            // At k=0.70 a 0.10 m cube is Req 237 mm (eaten by the start ball) and a 0.30 m
            // cube is Req 712 mm (not). Both hand-calculated from the original formula.
            Vector3 small = new Vector3(0.10f, 0.10f, 0.10f);
            Vector3 large = new Vector3(0.30f, 0.30f, 0.30f);
            Assert.IsFalse(PickupAbsorbMath.IsBeyondAbsorbLimit(small,
                PickupAbsorbMath.DefaultVolumeK, diameter));
            Assert.IsTrue(PickupAbsorbMath.IsBeyondAbsorbLimit(large,
                PickupAbsorbMath.DefaultVolumeK, diameter));

            // MEANING (pinned): "confirmed" is the ledger growth ABOVE the start ball, so a
            // fresh ball has 0 confirmed and a TOTAL of StartMm3. The gate/size model read
            // the total; the ledger reads the confirmed part.
            Assert.AreEqual(StartMm3, (long)GetField("startVolumeMm3"));
            Assert.AreEqual(0L, (long)InvokeGetter("GetConfirmedVolumeMm3"),
                "with nothing absorbed the ledger growth is zero");
            Assert.AreEqual(StartMm3, (long)InvokeGetter("GetTotalVolumeMm3"),
                "the total volume of the start ball is its start volume");
            Assert.AreEqual((float)(StartMm3 / 1e9),
                (float)InvokeGetter("GetTotalVolumeCubicMetres"), 1e-6f);
        }

        [Test]
        public void TheGlobalCapOverridesLegacySceneValuesAndSaturates()
        {
            Assert.AreEqual(400f, (float)GetField("maxBallDiameterM"));
            SetField("maxBallDiameterM", 30f); // old serialized scenes
            Invoke("Start");
            Assert.AreEqual(400f, (float)GetField("maxBallDiameterM"));
            Assert.AreEqual(200f, (float)GetField("maxCollisionRadiusWorldM"));

            // Credit more than the full ball volume and verify size saturation, not ledger truncation.
            const long growthMm3 = 33510321638291128L;
            SetField("unbankedVolumeUnits", growthMm3);
            Invoke("RecomputeConfirmedAndSize");
            Assert.AreEqual(400f, Getter("GetSemanticDiameterWorldM"));
            Assert.AreEqual(400000f, Getter("GetAbsorbGateDiameterMm"));
            Assert.AreEqual(growthMm3, (long)InvokeGetter("GetConfirmedVolumeMm3"));
        }

        // -- reflection helpers -------------------------------------------------

        private FieldInfo FieldOrNull(string name)
        {
            return motorType.GetField(name,
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        }

        private object GetField(string name)
        {
            FieldInfo fieldInfo = FieldOrNull(name);
            Assert.IsNotNull(fieldInfo, name + " field not found on KatamariBallMotor");
            return fieldInfo.GetValue(motor);
        }

        private void SetField(string name, object value)
        {
            FieldInfo fieldInfo = FieldOrNull(name);
            Assert.IsNotNull(fieldInfo, name + " field not found on KatamariBallMotor");
            fieldInfo.SetValue(motor, value);
        }

        private void Invoke(string name)
        {
            MethodInfo method = motorType.GetMethod(name, BindingFlags.Instance |
                BindingFlags.NonPublic | BindingFlags.Public);
            Assert.IsNotNull(method, name + " method not found on KatamariBallMotor");
            method.Invoke(motor, null);
        }

        private float Getter(string name)
        {
            return (float)InvokeGetter(name);
        }

        private object InvokeGetter(string name)
        {
            MethodInfo method = motorType.GetMethod(name, BindingFlags.Instance |
                BindingFlags.NonPublic | BindingFlags.Public);
            Assert.IsNotNull(method, name + " getter not found on KatamariBallMotor");
            return method.Invoke(motor, null);
        }
    }
}
