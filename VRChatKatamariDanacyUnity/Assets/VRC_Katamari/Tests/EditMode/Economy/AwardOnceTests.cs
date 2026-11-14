using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace VRCKatamari.Tests
{
    /// <summary>
    /// Pins the settlement award contract: a semantic key awards at most once.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The key is a label ("settle:first"), never a placement number. Settlements get
    /// retried (reconnect, scene reload, replay); a numeric key would award the same bonus
    /// again on every retry, whereas a semantic key makes "exactly once" a structural
    /// property instead of caller discipline.
    /// </para>
    /// <para>
    /// Reflection only: this test assembly deliberately does not reference
    /// UdonSharp.Runtime or VRCKatamari.Runtime.
    /// </para>
    /// <para>
    /// NOT COVERED: failureCode 5 (ledgerRefused, produced by the motor when the row's
    /// banked value is negative) and 4 (capacityFull, needs 16 distinct awarded keys
    /// through the motor's rule path). Neither is reachable from a unit test without
    /// faking ledger state, and neither is pretended here.
    /// </para>
    /// </remarks>
    public class AwardOnceTests
    {
        private const string LedgerTypeName = "VRCKatamari.Economy.GrowthLedgerMath, VRCKatamari.Runtime";

        private MethodInfo awardOnce;

        [SetUp]
        public void SetUp()
        {
            Type ledger = Type.GetType(LedgerTypeName);
            if (ledger == null)
            {
                foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
                {
                    Type t0 = a.GetType("VRCKatamari.Economy.GrowthLedgerMath");
                    if (t0 != null) { ledger = t0; break; }
                }
            }
            Assert.IsNotNull(ledger, "GrowthLedgerMath type not loaded");
            awardOnce = ledger.GetMethod("TryAwardOnce");
            Assert.IsNotNull(awardOnce, "TryAwardOnce not found");
        }

        /// <summary>Calls TryAwardOnce and returns the failure code; result via out param.</summary>
        private int Call(string[] keys, int[] points, int count, string key, int amount, out int newCount)
        {
            object[] args = new object[] { keys, points, count, key, amount, 0, 0 };
            awardOnce.Invoke(null, args);
            newCount = (int)args[5];
            return (int)args[6];
        }

        [Test]
        public void TheSameKeyAwardsOnlyOnce()
        {
            string[] keys = new string[16];
            int[] points = new int[16];
            int count = 0;

            int first;
            Assert.AreEqual(0, Call(keys, points, count, "settle:first", 50, out first),
                "the first award must succeed");
            Assert.AreEqual(1, first);

            int second;
            Assert.AreEqual(1, Call(keys, points, first, "settle:first", 50, out second),
                "a repeated key must report duplicateKey(1)");
            Assert.AreEqual(1, second, "a duplicate must not consume another slot");
        }

        [Test]
        public void DifferentKeysAccumulate()
        {
            string[] keys = new string[16];
            int[] points = new int[16];

            int afterFirst;
            Assert.AreEqual(0, Call(keys, points, 0, "settle:first", 50, out afterFirst));
            int afterSecond;
            Assert.AreEqual(0, Call(keys, points, afterFirst, "settle:second", 50, out afterSecond));
            int afterThird;
            Assert.AreEqual(0, Call(keys, points, afterSecond, "settle:third", 50, out afterThird));

            Assert.AreEqual(3, afterThird, "three distinct keys must be recorded");
            Assert.AreEqual(50, points[0]);
            Assert.AreEqual(50, points[1]);
            Assert.AreEqual(50, points[2]);
        }

        [Test]
        public void NonPositivePointsAreRefused()
        {
            string[] keys = new string[16];
            int[] points = new int[16];
            int count;
            Assert.AreEqual(2, Call(keys, points, 0, "settle:first", 0, out count),
                "zero points must report nonPositivePoints(2)");
            Assert.AreEqual(2, Call(keys, points, 0, "settle:first", -5, out count),
                "negative points must report nonPositivePoints(2)");
            Assert.AreEqual(0, count, "a refused award must not consume a slot");
        }

        [Test]
        public void ARefusedRowDoesNotConsumeTheKey()
        {
            // Pins the ordering fix: TryAwardScore validates the row BEFORE recording the key,
            // so a refusal for a bad row leaves the key free. The earlier order burned the key
            // and made the bonus unrecoverable after a retry.
            Type motorType = Type.GetType("VRCKatamari.BallMotor.KatamariBallMotor, VRCKatamari.Runtime");
            Assert.IsNotNull(motorType, "motor type not loaded");
            GameObject host = new GameObject("AwardRowOrderProbe");
            try
            {
                Component motor = host.AddComponent(motorType);
                const BindingFlags any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
                motorType.GetMethod("Start", any).Invoke(motor, null);
                motorType.GetMethod("TryBeginPlaying", any).Invoke(motor, new object[] { Time.time });

                int row = (int)motorType.GetField("localRow", any).GetValue(motor);
                int[] banked = (int[])motorType.GetField("bankedScores", any).GetValue(motor);
                MethodInfo award = motorType.GetMethod("TryAwardScore", any);
                Assert.IsNotNull(award, "TryAwardScore not found");

                banked[row] = -1;
                Assert.IsFalse((bool)award.Invoke(motor, new object[] { "settle:first", 50 }),
                    "a negative row must refuse the award");
                Assert.AreEqual(0, (int)motorType.GetMethod("GetAwardedAwardCount", any).Invoke(motor, null),
                    "a refused award must not consume the key");

                banked[row] = 0;
                Assert.IsTrue((bool)award.Invoke(motor, new object[] { "settle:first", 50 }),
                    "the same key must still be grantable after the row is fixed");
                Assert.AreEqual(1, (int)motorType.GetMethod("GetAwardedAwardCount", any).Invoke(motor, null));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(host);
            }
        }

        [Test]
        public void AnEmptyKeyIsRefused()
        {
            string[] keys = new string[16];
            int[] points = new int[16];
            int count;
            Assert.AreEqual(3, Call(keys, points, 0, "", 50, out count),
                "an empty key must report invalidKey(3)");
            Assert.AreEqual(3, Call(keys, points, 0, null, 50, out count),
                "a null key must report invalidKey(3)");
            Assert.AreEqual(0, count);
        }
    }
}
