using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace VRCKatamari.Tests
{
    /// <summary>
    /// Pins the conversion (bank) refusal reasons.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The reason codes are the seam that stops a caller from guessing: before them a
    /// boolean could not tell "the ball is too small" from "there is nothing to bank"
    /// from "the ledger refused", so any verification in a lab could only infer.
    /// </para>
    /// <para>
    /// Reflection only: this test assembly does not reference UdonSharp.Runtime or
    /// VRCKatamari.Runtime (adding those references just to construct a behaviour was
    /// already rejected once for pulling the whole SDK into the tests).
    /// </para>
    /// <para>
    /// NOT COVERED: reason 1 (noLocalRow), 6 (sizingRefused) and 8 (shrinkFailed).
    /// 1 needs a motor that never joined, which Start() makes impossible; 6 and 8 need
    /// non-finite sizing inputs or a ledger commit that succeeds while the shrink fails,
    /// neither reachable from a unit test without faking the ledger. Not pretended.
    ///
    /// NOT COVERED: reason 1 (noLocalRow). Start() always TryJoins with capacity one, so
    /// a motor without a local row is not constructible from a test.
    /// </para>
    /// </remarks>
    public class BankReasonTests
    {
        private const string MotorTypeName = "VRCKatamari.BallMotor.KatamariBallMotor, VRCKatamari.Runtime";
        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

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
                    Type t0 = a.GetType("VRCKatamari.BallMotor.KatamariBallMotor");
                    if (t0 != null) { motorType = t0; break; }
                }
            }
            Assert.IsNotNull(motorType, "KatamariBallMotor type not loaded");

            host = new GameObject("BankReasonTests");
            motor = host.AddComponent(motorType);
            Invoke("Start");

            // Start() opens the round at now = 0, and a zero-length countdown is refused,
            // so the ledger stays out of Playing and every bank would report
            // ledgerRefused(7). Move it to Playing with a real timestamp first.
            object started = Invoke("TryBeginPlaying", Time.time);
            Assert.AreEqual(true, started, "the ledger must reach Playing before a bank test");
        }

        [TearDown]
        public void TearDown()
        {
            if (host != null) UnityEngine.Object.DestroyImmediate(host);
        }

        private object Invoke(string name, params object[] args)
        {
            MethodInfo m = motorType.GetMethod(name, Any);
            Assert.IsNotNull(m, name + " not found");
            return m.Invoke(motor, args);
        }

        private void SetField(string name, object value)
        {
            FieldInfo f = motorType.GetField(name, Any);
            Assert.IsNotNull(f, name + " field not found");
            f.SetValue(motor, value);
        }

        private float GetField(string name)
        {
            FieldInfo f = motorType.GetField(name, Any);
            Assert.IsNotNull(f, name + " field not found");
            return (float)f.GetValue(motor);
        }

        /// <summary>Baseline units that put the start ball at a chosen semantic diameter.</summary>
        private void SetStartDiameter(float diameterM, float cubicPerUnit)
        {
            float volume = (4f / 3f) * Mathf.PI * Mathf.Pow(diameterM * 0.5f, 3f);
            SetField("cubicMetersPerVolumeUnit", cubicPerUnit);
            SetField("baselineVolumeUnits", volume / cubicPerUnit);
            Invoke("RecomputeConfirmedAndSize");
        }

        /// <summary>
        /// The ledger validates against its own confirmed array, not against the motor's
        /// cached field, so a unit test has to set both to describe "5 units confirmed".
        /// </summary>
        private void SetConfirmedUnits(int units)
        {
            SetField("unbankedVolumeUnits", units);
            FieldInfo f = motorType.GetField("confirmedVolumes", Any);
            Assert.IsNotNull(f, "confirmedVolumes field not found");
            int[] volumes = (int[])f.GetValue(motor);
            int row = (int)motorType.GetField("localRow", Any).GetValue(motor);
            volumes[row] = units;
        }

        private int BankReason(string requestId, int unbanked)
        {
            SetConfirmedUnits(unbanked);
            object[] args = new object[] { Time.time, requestId, 0 };
            Invoke("TryBankWithReason", args);
            return (int)args[2];
        }

        [Test]
        public void ATinyBallRefusesWithTooSmall()
        {
            SetStartDiameter(0.53f, 0.0041f);
            Assert.Less(GetField("semanticDiameterWorldM"), 0.55f, "the probe ball must start under the gate");
            Assert.AreEqual(4, BankReason("r-too-small", 5),
                "a ball under minConversionDiameterWorldM must report tooSmall(4)");
        }

        [Test]
        public void ABigEnoughBallWithVolumeSucceeds()
        {
            SetStartDiameter(0.57f, 0.0041f);
            Assert.Greater(GetField("semanticDiameterWorldM"), 0.55f, "the probe ball must start over the gate");
            Assert.AreEqual(0, BankReason("r-ok", 5), "a bank attempt with volume must report ok(0)");
        }

        [Test]
        public void ANonFiniteNowRefusesAsInvalidInput()
        {
            SetStartDiameter(0.57f, 0.0041f);
            SetConfirmedUnits(5);
            object[] args = new object[] { float.NaN, "r-nan", 0 };
            Invoke("TryBankWithReason", args);
            Assert.AreEqual(3, (int)args[2], "a NaN timestamp must report invalidInput(3)");
        }

        [Test]
        public void AnEmptyRequestIdRefuses()
        {
            SetStartDiameter(0.57f, 0.0041f);
            Assert.AreEqual(2, BankReason("", 5), "an empty request id must report invalidRequestId(2)");
        }

        [Test]
        public void WithNothingUnbankedTheRefusalIsNothingToBank()
        {
            SetStartDiameter(0.57f, 0.0041f);
            Assert.AreEqual(5, BankReason("r-empty", 0),
                "a finished conversion leaves nothing to bank, which is nothingToBank(5)");
        }
    }
}
