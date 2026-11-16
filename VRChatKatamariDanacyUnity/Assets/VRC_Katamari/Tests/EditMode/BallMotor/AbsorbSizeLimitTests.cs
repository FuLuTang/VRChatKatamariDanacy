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
        public void TheCapIsTwoPointTwoMetresOnDemandAndThirtyMetresByDefault()
        {
            // The motor's default cap is the ONE global parameter (30 m).
            Assert.AreEqual(30f, (float)GetField("maxBallDiameterM"), 1e-6f);

            // A level may pin a smaller cap through that same field; the 2.20 m content
            // ladder is then a data choice, not a second code path.
            SetField("maxBallDiameterM", 2.2f);
            // Drive the size stage to the cap: pi/6 * 2.2^3 = 5,575,279,763 mm^3 total, minus
            // the start volume. Writing the ledger growth and recomputing is exactly what a
            // committed absorb does.
            SetField("unbankedVolumeUnits", 5575279763L - StartMm3);
            Invoke("RecomputeConfirmedAndSize");

            float diameter = Getter("GetSemanticDiameterWorldM");
            Assert.AreEqual(2.2f, diameter, 0f, "saturation is exact, not approximate");
            Assert.AreEqual(2200f, Getter("GetAbsorbGateDiameterMm"), 1e-6f);
        }

        [Test]
        public void AFullyGrownBallCanTakeAnObjectTheOldCeilingWouldHaveRefused()
        {
            // A 5 m cube is 125 m^3 of bbox; at k=0.70 the item volume is 87.5 m^3 and the
            // 30 m ball's gate allows up to (pi/6*30^3)/10 = 1,413.7 m^3, so it is food. The
            // retired 512 m^3 ceiling would also have passed this one, but the SANITY bound
            // must not: a broken 100 m cube (1,000,000 m^3) is beyond it and reads blocking.
            // The ball must actually BE at the 30 m cap: this test's first version left the
            // ledger at zero and then judged a 5 m cube with a 0.30 m ball, which flipped the
            // verdict for the right reason (0.3 m ball, huge cube).
            const long Cap30Mm3 = 14137166941154L;
            SetField("unbankedVolumeUnits", Cap30Mm3 - StartMm3);
            Invoke("RecomputeConfirmedAndSize");
            float diameter = Getter("GetSemanticDiameterWorldM");
            Vector3 huge = new Vector3(5f, 5f, 5f);
            Assert.IsFalse(PickupAbsorbMath.IsBeyondAbsorbLimit(huge,
                PickupAbsorbMath.DefaultVolumeK, diameter),
                "a 5 m cube is absorbable by a 30 m ball");
            Vector3 broken = new Vector3(100f, 100f, 100f);
            Assert.IsTrue(PickupAbsorbMath.IsBeyondAbsorbLimit(broken,
                PickupAbsorbMath.DefaultVolumeK, diameter),
                "the sanity bound must refuse a broken bounds triple, not call it infinite");
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
