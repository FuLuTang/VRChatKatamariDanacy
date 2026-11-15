using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace VRCKatamari.Tests
{
    /// <summary>
    /// Pins the motor's absorb size gate to the ball's own size (Docs/OPEN_DECISIONS.md
    /// (h)): the serialized ceiling is no longer the gameplay threshold, and the ratio
    /// default is 1.0.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Like CandidateQueueIdempotencyTests, the motor is reached through reflection: this
    /// test assembly does not reference UdonSharp.Runtime, so constructing the behaviour
    /// directly would drag the SDK in. Only the public getters and the two serialized
    /// fields are touched.
    /// </para>
    /// <para>
    /// What this file is for: the retired gate was an absolute 1 m comparison, so a 2.20 m
    /// ball still could not eat a 1.2 m prop. The numbers below are the two facts that
    /// keep the fix from being undone by a serialized default: the ceiling must be at
    /// least the full ball size, and the limit must track the semantic diameter. The
    /// full-ball case writes the same 1356 confirmed units SizeCalibrationTests pins as
    /// the 2.20 m cap.
    /// </para>
    /// </remarks>
    public class AbsorbSizeLimitTests
    {
        private const string MotorTypeName =
            "VRCKatamari.BallMotor.KatamariBallMotor, VRCKatamari.Runtime";

        // 1356 confirmed units is the 2.20 m cap (SizeCalibrationTests, round_model.py).
        private const float UnitsAtCap = 1356f;

        private GameObject host;
        private Component motor;
        private Type motorType;

        [SetUp]
        public void SetUp()
        {
            motorType = Type.GetType(MotorTypeName);
            if (motorType == null)
            {
                foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
                {
                    Type found = a.GetType("VRCKatamari.BallMotor.KatamariBallMotor");
                    if (found != null) { motorType = found; break; }
                }
            }
            Assert.IsNotNull(motorType, "KatamariBallMotor type not loaded");

            host = new GameObject("AbsorbSizeLimitTests");
            motor = host.AddComponent(motorType);
            Assert.IsNotNull(motor, "AddComponent(KatamariBallMotor) returned null");

            MethodInfo start = motorType.GetMethod("Start",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            Assert.IsNotNull(start, "Start not found; sizes would stay uninitialised");
            start.Invoke(motor, null);
        }

        [TearDown]
        public void TearDown()
        {
            if (host != null) UnityEngine.Object.DestroyImmediate(host);
        }

        [Test]
        public void TheCeilingIsAboveTheFullBallSize()
        {
            // A ceiling at or below 2.20 m would re-create the retired absolute gate at
            // the cap, so it is asserted against the cap diameter, not against a number
            // copied from the field.
            Assert.GreaterOrEqual(Field("maxItemSizeWorldM"), 2.20f,
                "the serialized ceiling must not bind a full-size ball");
            // 1.0 is the PROVISIONAL gameplay ratio (decision (h)).
            Assert.AreEqual(1f, Field("absorbSizeRatio"), 1e-6f);
        }

        [Test]
        public void TheStartBallLimitIsItsOwnDiameter()
        {
            float diameter = Getter("GetSemanticDiameterWorldM");
            float limit = Getter("GetAbsorbSizeLimitWorldM");
            Assert.AreEqual(0.29974f, diameter, 0.005f);
            // diameter * 1.0, nowhere near the 8 m ceiling.
            Assert.AreEqual(diameter, limit, 1e-5f);
        }

        [Test]
        public void AFullyGrownBallCanTakeAnItemOverOneMetre()
        {
            // Drive the size stage to the cap the way a round would: confirmed units are
            // the only input, so writing the unbanked total and recomputing is exactly
            // what a committed absorb does.
            SetField("unbankedVolumeUnits", (int)UnitsAtCap);
            Invoke("RecomputeConfirmedAndSize");

            float diameter = Getter("GetSemanticDiameterWorldM");
            float limit = Getter("GetAbsorbSizeLimitWorldM");
            // 2.20 m is the cap SizeCalibrationTests pins for 1356 units.
            Assert.AreEqual(2.20f, diameter, 0.01f);
            // The old gate answered 1.0 here and refused any 1.2 m prop; the relative
            // gate answers the ball's diameter.
            Assert.AreEqual(2.20f, limit, 0.01f);
            Assert.Greater(limit, 1.2f, "a 1.2 m prop must be within a full ball's limit");
        }

        [Test]
        public void TheRatioScalesTheLimitAndANonPositiveRatioIsRefused()
        {
            float diameter = Getter("GetSemanticDiameterWorldM");
            SetField("absorbSizeRatio", 0.5f);
            Assert.AreEqual(diameter * 0.5f, Getter("GetAbsorbSizeLimitWorldM"), 1e-5f);

            // Zero and negative are invalid input, not "absorb nothing": the getter
            // reports -1 so a reader cannot mistake a broken config for a small ball.
            SetField("absorbSizeRatio", 0f);
            Assert.AreEqual(-1f, Getter("GetAbsorbSizeLimitWorldM"), 1e-6f);
            SetField("absorbSizeRatio", -1f);
            Assert.AreEqual(-1f, Getter("GetAbsorbSizeLimitWorldM"), 1e-6f);
        }

        private float Field(string name)
        {
            FieldInfo field = motorType.GetField(name,
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            Assert.IsNotNull(field, "field " + name + " not found");
            return (float)field.GetValue(motor);
        }

        private void SetField(string name, float value)
        {
            FieldInfo field = motorType.GetField(name,
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            Assert.IsNotNull(field, "field " + name + " not found");
            field.SetValue(motor, value);
        }

        private void SetField(string name, int value)
        {
            FieldInfo field = motorType.GetField(name,
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            Assert.IsNotNull(field, "field " + name + " not found");
            field.SetValue(motor, value);
        }

        private float Getter(string name)
        {
            MethodInfo method = motorType.GetMethod(name,
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            Assert.IsNotNull(method, "getter " + name + " not found");
            return (float)method.Invoke(motor, null);
        }

        private void Invoke(string name)
        {
            MethodInfo method = motorType.GetMethod(name,
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            Assert.IsNotNull(method, "method " + name + " not found");
            method.Invoke(motor, null);
        }
    }
}
