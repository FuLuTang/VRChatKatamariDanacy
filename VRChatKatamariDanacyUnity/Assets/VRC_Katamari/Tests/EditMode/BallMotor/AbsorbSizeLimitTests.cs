using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace VRCKatamari.Tests
{
    /// <summary>
    /// Pins the motor's absorb size gate to the ball's own volume
    /// (Docs/OPEN_DECISIONS.md (l), which supersedes (h)): the gate is
    /// `a*b*c &lt;= (pi/6) * D^3 / absorbVolumeRatio`, the serialized ceiling is a volume
    /// in cubic metres and not the gameplay threshold, and the ratio default is 10.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Like CandidateQueueIdempotencyTests, the motor is reached through reflection: this
    /// test assembly does not reference UdonSharp.Runtime, so constructing the behaviour
    /// directly would drag the SDK in. Only the public getters and the two serialized
    /// fields are touched.
    /// </para>
    /// <para>
    /// What this file is for: the retired gate was first an absolute 1 m comparison, then
    /// a linear `diameter * ratio` one, and both were undone by a serialized default at
    /// least once. The numbers below are the facts that keep the volume gate from being
    /// undone the same way: the ceiling must be above a full ball's VOLUME, and the limit
    /// must equal the ball's own volume over the ratio. Every expected value comes from
    /// the growth model's own definition of the ball volume
    /// (`(baselineVolumeUnits + units) * cubicMetersPerVolumeUnit`, KatamariBallMotor.cs
    /// fields 105-106), not from the gate formula, so a formula change has to disagree
    /// with the document rather than with a copy of itself.
    /// </para>
    /// </remarks>
    public class AbsorbSizeLimitTests
    {
        private const string MotorTypeName =
            "VRCKatamari.BallMotor.KatamariBallMotor, VRCKatamari.Runtime";

        // 1356 confirmed units is the 2.20 m cap (SizeCalibrationTests, round_model.py).
        private const float UnitsAtCap = 1356f;

        // The ball's volume is (baselineVolumeUnits + units) * cubicMetersPerVolumeUnit,
        // i.e. 3.4390244 + units, times 0.0041 m^3. Zero units is the 0.29974 m start
        // ball: 3.4390244 * 0.0041 = 0.0141 m^3. At the cap: 1359.4390244 * 0.0041 =
        // 5.5737 m^3, and (pi/6) * 2.20^3 = 5.5752 m^3 agrees to four digits, which is
        // the same volume reached two independent ways.
        private const float StartBallVolumeWorldM3 = 0.0141f;
        private const float CapBallVolumeWorldM3 = 5.5737f;

        // The ratio the research note reads off the original: the ball's volume must be
        // ten times the item's (Docs/Research/KatamariBallRules/ABSORB_SIZE_RATIO.md).
        private const float OriginalVolumeRatio = 10f;

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
        public void TheCeilingIsAboveTheFullBallVolume()
        {
            // A ceiling at or below the full ball's volume would re-create the retired
            // absolute gate at the cap, so it is asserted against that volume, not against
            // a number copied from the field.
            Assert.GreaterOrEqual(Field("maxItemVolumeWorldM3"), CapBallVolumeWorldM3,
                "the serialized volume ceiling must not bind a full-size ball");
            // 10 is the original's volume ratio (decision (l)); 1.0 here would be the
            // retired linear ratio's scale wearing the new field's name.
            Assert.AreEqual(OriginalVolumeRatio, Field("absorbVolumeRatio"), 1e-6f);
        }

        [Test]
        public void TheStartBallLimitIsATenthOfItsOwnVolume()
        {
            float diameter = Getter("GetSemanticDiameterWorldM");
            float limit = Getter("GetAbsorbVolumeLimitWorldM3");
            Assert.AreEqual(0.29974f, diameter, 0.005f);
            // 0.0141 m^3 / 10 = 0.00141 m^3, nowhere near the 512 m^3 ceiling.
            Assert.AreEqual(StartBallVolumeWorldM3 / OriginalVolumeRatio, limit, 1e-6f);
        }

        [Test]
        public void AFullyGrownBallCanTakeAOnePointTwoMetrePlank()
        {
            // Drive the size stage to the cap the way a round would: confirmed units are
            // the only input, so writing the unbanked total and recomputing is exactly
            // what a committed absorb does.
            SetField("unbankedVolumeUnits", (long)UnitsAtCap);
            Invoke("RecomputeConfirmedAndSize");

            float diameter = Getter("GetSemanticDiameterWorldM");
            float limit = Getter("GetAbsorbVolumeLimitWorldM3");
            // 2.20 m is the cap SizeCalibrationTests pins for 1356 units.
            Assert.AreEqual(2.20f, diameter, 0.01f);
            // 5.5737 m^3 / 10 = 0.5574 m^3. The retired linear gate answered 1.0 here (a
            // length) and refused a 1.2 m prop; the volume gate answers a volume, and a
            // 1.2 x 0.1 x 0.1 m plank is 0.012 m^3, i.e. 46 times inside the limit.
            Assert.AreEqual(CapBallVolumeWorldM3 / OriginalVolumeRatio, limit, 0.002f);
            Assert.Greater(limit, 0.012f, "a 1.2 m plank must be within a full ball's limit");
        }

        [Test]
        public void TheVolumeRatioScalesTheLimitAndANonPositiveRatioIsRefused()
        {
            float atDefault = Getter("GetAbsorbVolumeLimitWorldM3");
            // Halving the ratio doubles the limit: the ball only has to be five times the
            // item's volume.
            SetField("absorbVolumeRatio", OriginalVolumeRatio * 0.5f);
            Assert.AreEqual(atDefault * 2f, Getter("GetAbsorbVolumeLimitWorldM3"), 1e-8f);

            // Zero and negative are invalid input, not "absorb nothing": the getter
            // reports -1 so a reader cannot mistake a broken config for a small ball.
            SetField("absorbVolumeRatio", 0f);
            Assert.AreEqual(-1f, Getter("GetAbsorbVolumeLimitWorldM3"), 1e-6f);
            SetField("absorbVolumeRatio", -1f);
            Assert.AreEqual(-1f, Getter("GetAbsorbVolumeLimitWorldM3"), 1e-6f);
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

        // The ledger mirror is 64-bit (Docs/OPEN_DECISIONS.md (r) step A): reflection
        // assigns a boxed long to it, never a boxed int, which would throw.
        private void SetField(string name, long value)
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
