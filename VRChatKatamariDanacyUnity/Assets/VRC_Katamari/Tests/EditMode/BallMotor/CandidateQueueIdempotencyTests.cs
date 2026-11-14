using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace VRCKatamari.Tests
{
    /// <summary>
    /// Pins the candidate queue's idempotent submit contract.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The host used to keep its own "I am queued" latch next to the motor's queue.
    /// Two copies of one fact diverge, and they did: the latch said queued while the
    /// queue held nothing, so the item was never submitted again and the ball rolled
    /// over it without absorbing. The fix is that submitting twice is a no-op, so a
    /// host needs no latch at all.
    /// </para>
    /// <para>
    /// The motor is reached purely through reflection: this test assembly does not
    /// reference UdonSharp.Runtime, and adding that reference just to construct a
    /// behaviour would couple the tests to the SDK. Reflection keeps the test to the
    /// public queue API (SubmitCandidate / WithdrawCandidate / the pending getters).
    /// </para>
    /// </remarks>
    public class CandidateQueueIdempotencyTests
    {
        private const string MotorTypeName = "VRCKatamari.BallMotor.KatamariBallMotor, VRCKatamari.Runtime";

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

            host = new GameObject("CandidateQueueIdempotencyTests");
            motor = host.AddComponent(motorType);
            Assert.IsNotNull(motor, "AddComponent(KatamariBallMotor) returned null");

            MethodInfo start = motorType.GetMethod("Start",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            Assert.IsNotNull(start, "Start not found; the queue would stay unallocated");
            start.Invoke(motor, null);
        }

        [TearDown]
        public void TearDown()
        {
            if (host != null) UnityEngine.Object.DestroyImmediate(host);
        }

        private object Call(string name, params object[] args)
        {
            MethodInfo m = motorType.GetMethod(name);
            Assert.IsNotNull(m, name + " not found on KatamariBallMotor");
            return m.Invoke(motor, args);
        }

        private bool Submit(string id)
        {
            return (bool)Call("SubmitCandidate", true, "small",
                new Vector3(0.1f, 0.1f, 0.1f), new Vector3(0f, 0.05f, 1f), 3f, id, 0);
        }

        private int Count()
        {
            return (int)Call("GetPendingCandidateCount");
        }

        private string ItemIdAt(int index)
        {
            return (string)Call("GetPendingCandidateItemId", index);
        }

        [Test]
        public void SubmittingTheSameIdTwiceKeepsOneSlotAndSucceedsBothTimes()
        {
            Assert.IsTrue(Submit("dup"), "first submit must be accepted");
            Assert.AreEqual(1, Count());

            Assert.IsTrue(Submit("dup"), "a duplicate submit must report success, not a refusal");
            Assert.AreEqual(1, Count(), "a duplicate must not take a second slot");
            Assert.AreEqual("dup", ItemIdAt(0));
        }

        [Test]
        public void WithdrawnIdCanBeSubmittedAgain()
        {
            Assert.IsTrue(Submit("again"));
            Assert.IsTrue((bool)Call("WithdrawCandidate", "again"));
            Assert.AreEqual(0, Count());

            Assert.IsTrue(Submit("again"), "re-submitting after a withdraw must succeed");
            Assert.AreEqual(1, Count());
            Assert.AreEqual("again", ItemIdAt(0));
        }

        [Test]
        public void WithdrawingAnIdThatIsNotQueuedIsAHarmlessNoOp()
        {
            Assert.IsTrue(Submit("present"));
            Assert.IsFalse((bool)Call("WithdrawCandidate", "absent"),
                "withdrawing an absent id must report false, not throw");
            Assert.AreEqual(1, Count(), "an absent withdraw must not touch the queue");
            Assert.AreEqual("present", ItemIdAt(0));
        }
    }
}
