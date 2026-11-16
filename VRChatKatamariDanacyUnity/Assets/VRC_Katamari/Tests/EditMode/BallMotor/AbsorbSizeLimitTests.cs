using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using VRCKatamari.Pickup;

namespace VRCKatamari.Tests
{
    /// <summary>
    /// Pins the motor side of the ORIGINAL absorb gate (owner decision 2026-11-16): the
    /// ball's semantic diameter becomes a whole-millimetre number, the retired per-config
    /// ratio/ceiling fields are gone, and the gate's constants are the original's.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Like CandidateQueueIdempotencyTests, the motor is reached through reflection: this
    /// test assembly does not reference UdonSharp.Runtime, so constructing the behaviour
    /// directly would drag the SDK in. Only public getters and the ledger mirror field are
    /// touched.
    /// </para>
    /// <para>
    /// What this file is for: the retired gate was first an absolute 1 m comparison, then a
    /// linear `diameter * ratio` one, then a ball-relative volume limit with a serialized
    /// 512 m^3 ceiling - and every one of them was undone by a serialized default at least
    /// once. The facts below are what keep the original rule from being undone the same
    /// way: the ball side is a whole-millimetre DIAMETER (not a volume limit, not a
    /// serialized knob), the rule constants are constants, and the retired fields are
    /// asserted absent so a reintroduced default fails here first.
    /// </para>
    /// <para>
    /// Every expected number is derived in the comment from the original's formula
    /// (`Req = floor(1000*cbrt(60*k*a*b*c/pi))`) and the project's size calibration, never
    /// copied from the implementation.
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
        // 5.5737 m^3, and (pi/6) * 2.20^3 = 5.5752 m^3 agrees to four digits.
        private const float StartBallVolumeWorldM3 = 0.0141f;
        private const float CapBallVolumeWorldM3 = 5.5737f;

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
        public void TheGateConstantsAreTheOriginals()
        {
            // 0.70 default factor and k <= 32 (PARITY_AUDIT G13), 10x the ball's volume
            // (G07), and a sanity ceiling that is NOT the retired 512 m^3 item cap.
            Assert.AreEqual(0.70f, PickupAbsorbMath.DefaultVolumeK, 1e-6f);
            Assert.AreEqual(32f, PickupAbsorbMath.MaxVolumeK, 1e-6f);
            Assert.AreEqual(10f, PickupAbsorbMath.AbsorbVolumeRatio, 1e-6f);
            Assert.AreEqual(1000f, PickupAbsorbMath.MaxItemVolumeSanityWorldM3, 1e-6f);

            // The ceiling is far above the largest item the ball cap can absorb:
            // (pi/6) * 2.20^3 / 10 = 0.5575 m^3, so 1000 m^3 is ~1,792x that. A ceiling at
            // or below the cap ball's item limit would re-create the retired coupling.
            float capItemLimit = CapBallVolumeWorldM3 / PickupAbsorbMath.AbsorbVolumeRatio;
            Assert.Greater(PickupAbsorbMath.MaxItemVolumeSanityWorldM3, capItemLimit * 500f,
                "the sanity ceiling must not bind a legal ball");
        }

        [Test]
        public void TheRetiredRatioAndCeilingFieldsAreGone()
        {
            // A scene or prefab may still serialize these names; Unity ignores dead keys.
            // A reintroduced FIELD would mean a config can silently stop being the
            // original's gate, which is exactly what the owner decision forbids.
            Assert.IsNull(motorType.GetField("maxItemVolumeWorldM3",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public),
                "the 512 m^3 item ceiling must stay retired");
            Assert.IsNull(motorType.GetField("absorbVolumeRatio",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public),
                "the serialized ratio must stay retired (PickupAbsorbMath.AbsorbVolumeRatio)");
            Assert.IsNull(motorType.GetMethod("GetAbsorbVolumeLimitWorldM3",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public),
                "the per-step volume limit is not the gate's quantity any more");
        }

        [Test]
        public void TheStartBallIsAWholeMillimetreDiameter()
        {
            float diameter = Getter("GetSemanticDiameterWorldM");
            float ballMm = Getter("GetAbsorbGateDiameterMm");
            Assert.AreEqual(0.29974f, diameter, 0.005f);
            // 0.29974 m * 1000 = 299.74 mm -> 299 mm (floored, as the original does).
            Assert.AreEqual(299f, ballMm, 1e-6f);

            // The start ball therefore eats a 0.10 m cube (Req 237 mm) and not a 0.30 m
            // cube (Req 712 mm) at k=0.70; both numbers come from
            // Req = floor(1000*cbrt(60*0.70*a*b*c/pi)).
            Vector3 small = new Vector3(0.10f, 0.10f, 0.10f);
            Vector3 large = new Vector3(0.30f, 0.30f, 0.30f);
            Assert.IsFalse(PickupAbsorbMath.IsBeyondAbsorbLimit(small,
                PickupAbsorbMath.DefaultVolumeK, diameter));
            Assert.IsTrue(PickupAbsorbMath.IsBeyondAbsorbLimit(large,
                PickupAbsorbMath.DefaultVolumeK, diameter));

            // The ball's own volume is still the calibrated 0.0141 m^3 - the gate no longer
            // reads it, but the size model that produces the diameter does.
            SetField("unbankedVolumeUnits", 0L);
            Invoke("RecomputeConfirmedAndSize");
            Assert.AreEqual(StartBallVolumeWorldM3,
                (float)InvokeGetter("GetConfirmedVolumeUnits") *
                    Field("cubicMetersPerVolumeUnit"), 1e-4f);
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
            float ballMm = Getter("GetAbsorbGateDiameterMm");
            // 2.20 m is the cap SizeCalibrationTests pins for 1356 units -> 2200 mm.
            Assert.AreEqual(2.20f, diameter, 0.01f);
            Assert.AreEqual(2200f, ballMm, 1e-6f);

            // A 1.2 x 0.1 x 0.1 m plank is 0.012 m^3 of bbox, 0.0084 m^3 at k=0.70, so
            // Req = floor(1000*cbrt(60*0.0084/pi)) = 543 mm, i.e. 4x inside the cap ball.
            Vector3 plank = new Vector3(1.2f, 0.1f, 0.1f);
            float reqMm;
            Assert.IsTrue(PickupAbsorbMath.TryComputePickupReqDiameterMm(plank,
                PickupAbsorbMath.DefaultVolumeK, out reqMm));
            Assert.AreEqual(543f, reqMm, 1e-6f);
            Assert.Less(reqMm, ballMm, "a 1.2 m plank must be within a full ball's gate");
            Assert.IsFalse(PickupAbsorbMath.IsBeyondAbsorbLimit(plank,
                PickupAbsorbMath.DefaultVolumeK, diameter));
        }

        [Test]
        public void AnInvalidRadiusStillReportsMinusOne()
        {
            // A broken size reading (zero diameter) is not a throw: the gate's ball side
            // reports -1 so a reader cannot mistake it for "a very small ball".
            Invoke("RecomputeConfirmedAndSize");
            SetField("semanticDiameterWorldM", float.NaN);
            Assert.AreEqual(-1f, Getter("GetAbsorbGateDiameterMm"), 1e-6f);
            SetField("semanticDiameterWorldM", -1f);
            Assert.AreEqual(-1f, Getter("GetAbsorbGateDiameterMm"), 1e-6f);
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

        private object InvokeGetter(string name)
        {
            MethodInfo method = motorType.GetMethod(name,
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            Assert.IsNotNull(method, "getter " + name + " not found");
            return method.Invoke(motor, null);
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
