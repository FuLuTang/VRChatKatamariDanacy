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

        [Test]
        public void WithdrawingInTheMiddleKeepsTheOrderAndFreesTheSlot()
        {
            // The withdraw path and the commit path compact the queue with one shared
            // helper, so this pins the order after a removal in the middle, then after
            // the freed slot is reused, then after removing an entry that is already at
            // its write position.
            Assert.IsTrue(Submit("a"));
            Assert.IsTrue(Submit("b"));
            Assert.IsTrue(Submit("c"));
            Assert.IsTrue(Submit("d"));
            Assert.AreEqual(4, Count(), "the queue holds four");

            Assert.IsTrue((bool)Call("WithdrawCandidate", "b"));
            Assert.AreEqual(3, Count());
            Assert.AreEqual("a", ItemIdAt(0));
            Assert.AreEqual("c", ItemIdAt(1));
            Assert.AreEqual("d", ItemIdAt(2));

            Assert.IsTrue(Submit("e"), "the freed slot must be usable again");
            Assert.AreEqual(4, Count());
            Assert.AreEqual("e", ItemIdAt(3));

            Assert.IsTrue((bool)Call("WithdrawCandidate", "c"));
            Assert.AreEqual(3, Count());
            Assert.AreEqual("a", ItemIdAt(0));
            Assert.AreEqual("d", ItemIdAt(1));
            Assert.AreEqual("e", ItemIdAt(2));

            // Removing the last entry copies nothing at all.
            Assert.IsTrue((bool)Call("WithdrawCandidate", "e"));
            Assert.AreEqual(2, Count());
            Assert.AreEqual("a", ItemIdAt(0));
            Assert.AreEqual("d", ItemIdAt(1));
        }

        // pendingAccepted is the gate stage's own flag (set in the gate stage, consumed by the
        // commit stage of the same step), so a test that wants "accepted and not yet
        // committed" without stepping the motor writes it directly.
        private void MarkAccepted(string id)
        {
            FieldInfo field = motorType.GetField("pendingAccepted",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            Assert.IsNotNull(field, "pendingAccepted not found");
            bool[] accepted = (bool[])field.GetValue(motor);
            for (int i = 0; i < Count(); i++)
            {
                if (ItemIdAt(i) == id) accepted[i] = true;
            }
        }

        [Test]
        public void EveryCandidateKeepsItsOwnRotationAndSizeThroughACompaction()
        {
            // The queue's per-candidate arrays are copied by one shared helper. The contact
            // test reads a candidate's size and rotation, so a field that is not copied
            // would judge a survivor with a removed candidate's box.
            string[] ids = new string[] { "a", "b", "c" };
            for (int i = 0; i < ids.Length; i++)
            {
                float degrees = 30f * (i + 1);
                float half = degrees * Mathf.PI / 360f;
                Quaternion rotation = new Quaternion(0f, Mathf.Sin(half), 0f, Mathf.Cos(half));
                Assert.IsTrue((bool)Call("SubmitCandidateWithVolumeKMm3", true, "small",
                    new Vector3(0.1f * (i + 1), 0.1f, 0.1f), rotation,
                    new Vector3(0f, 0.05f, 1f), 3000000L, ids[i], 0, 0.7f));
            }

            Assert.IsTrue((bool)Call("WithdrawCandidate", "a"));
            Vector3[] sizes = (Vector3[])motorType.GetField("pendingSizes",
                BindingFlags.Instance | BindingFlags.NonPublic).GetValue(motor);
            Quaternion[] rotations = (Quaternion[])motorType.GetField("pendingRotations",
                BindingFlags.Instance | BindingFlags.NonPublic).GetValue(motor);
            Assert.AreEqual("b", ItemIdAt(0));
            Assert.AreEqual(0.2f, sizes[0].x, 1e-6f, "b keeps its own size");
            Assert.AreEqual(Mathf.Sin(60f * Mathf.PI / 360f), rotations[0].y, 1e-6f,
                "b keeps its own rotation");
            Assert.AreEqual("c", ItemIdAt(1));
            Assert.AreEqual(0.3f, sizes[1].x, 1e-6f, "c keeps its own size");
            Assert.AreEqual(Mathf.Sin(90f * Mathf.PI / 360f), rotations[1].y, 1e-6f,
                "c keeps its own rotation");
        }

        [Test]
        public void WithdrawUnlessAcceptedRemovesAnOrdinaryCandidate()
        {
            Assert.IsTrue(Submit("a"));
            Assert.IsTrue(Submit("b"));

            Assert.IsTrue((bool)Call("WithdrawCandidateUnlessAccepted", "a"));
            Assert.AreEqual(1, Count());
            Assert.AreEqual("b", ItemIdAt(0));
            Assert.IsFalse((bool)Call("IsCandidateQueued", "a"));
        }

        [Test]
        public void WithdrawUnlessAcceptedKeepsAnAcceptedCandidateAndSaysItIsStillQueued()
        {
            Assert.IsTrue(Submit("a"));
            Assert.IsTrue(Submit("b"));
            Assert.IsTrue(Submit("c"));
            MarkAccepted("b");

            Assert.IsFalse((bool)Call("WithdrawCandidateUnlessAccepted", "b"),
                "an accepted candidate is a pending claim and is not removed");
            Assert.IsTrue((bool)Call("IsCandidateQueued", "b"),
                "false-but-queued is how a host tells 'held' from 'gone'");
            Assert.AreEqual(3, Count());
            Assert.AreEqual("b", ItemIdAt(1), "the order is untouched");

            // The round-end release is stronger: nothing may absorb once the round stops.
            Assert.IsTrue((bool)Call("WithdrawCandidate", "b"));
            Assert.AreEqual(2, Count());
            Assert.IsFalse((bool)Call("IsCandidateQueued", "b"));
        }

        [Test]
        public void WithdrawUnlessAcceptedOnAnAbsentIdIsAHarmlessNoOp()
        {
            Assert.IsTrue(Submit("a"));
            Assert.IsFalse((bool)Call("WithdrawCandidateUnlessAccepted", "absent"));
            Assert.IsFalse((bool)Call("IsCandidateQueued", "absent"));
            Assert.AreEqual(1, Count());
        }

        private T[] PendingArray<T>(string name)
        {
            FieldInfo field = motorType.GetField(name,
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            Assert.IsNotNull(field, name + " not found");
            return (T[])field.GetValue(motor);
        }

        private bool RefreshPose(string id, Vector3 position, Quaternion rotation)
        {
            return (bool)Call("RefreshCandidatePoseUnlessAccepted", id, position, rotation);
        }

        [Test]
        public void RefreshPoseWritesOnlyPositionAndRotationOfThatCandidate()
        {
            Assert.IsTrue(Submit("a"));
            Assert.IsTrue(Submit("b"));
            // A sentinel submit time: Time.time does not advance in EditMode, so only a
            // distinct value can show that the refresh left the time alone.
            float[] submitTimes = PendingArray<float>("pendingSubmitTimes");
            submitTimes[0] = -123f;
            submitTimes[1] = -456f;

            Quaternion turned = new Quaternion(0f, Mathf.Sin(Mathf.PI / 4f), 0f,
                Mathf.Cos(Mathf.PI / 4f));
            Vector3 moved = new Vector3(2f, 0.05f, 3f);
            Assert.IsTrue(RefreshPose("b", moved, turned));

            Vector3[] positions = PendingArray<Vector3>("pendingPositions");
            Quaternion[] rotations = PendingArray<Quaternion>("pendingRotations");
            bool[] accepted = PendingArray<bool>("pendingAccepted");
            Assert.AreEqual(moved, positions[1], "the refreshed pose is the new one");
            Assert.AreEqual(turned.y, rotations[1].y, 1e-6f);
            Assert.AreEqual(new Vector3(0f, 0.05f, 1f), positions[0], "other entries untouched");
            Assert.AreEqual(-456f, submitTimes[1], "a refresh is not a re-submission");
            Assert.AreEqual(-123f, submitTimes[0]);
            Assert.IsFalse(accepted[1], "the accepted flag is not written");
            Assert.AreEqual(2, Count());
            Assert.AreEqual("b", ItemIdAt(1), "the order is untouched");
        }

        [Test]
        public void RefreshPoseLeavesAnAcceptedCandidateAtItsJudgedPose()
        {
            Assert.IsTrue(Submit("a"));
            MarkAccepted("a");
            float[] submitTimes = PendingArray<float>("pendingSubmitTimes");
            submitTimes[0] = -7f;

            Assert.IsFalse(RefreshPose("a", new Vector3(5f, 0f, 5f), Quaternion.identity),
                "an accepted entry is a pending claim and keeps its pose");
            Assert.AreEqual(new Vector3(0f, 0.05f, 1f), PendingArray<Vector3>("pendingPositions")[0]);
            Assert.IsTrue(PendingArray<bool>("pendingAccepted")[0], "still accepted");
            Assert.AreEqual(-7f, submitTimes[0]);
            Assert.AreEqual(1, Count());
        }

        [Test]
        public void RefreshPoseOnAnUnknownOrEmptyIdReturnsFalseAndChangesNothing()
        {
            Assert.IsTrue(Submit("a"));
            Assert.IsFalse(RefreshPose("absent", new Vector3(9f, 0f, 9f), Quaternion.identity));
            Assert.IsFalse(RefreshPose("", new Vector3(9f, 0f, 9f), Quaternion.identity));
            Assert.IsFalse(RefreshPose(null, new Vector3(9f, 0f, 9f), Quaternion.identity));
            Assert.AreEqual(new Vector3(0f, 0.05f, 1f), PendingArray<Vector3>("pendingPositions")[0]);
            Assert.AreEqual(1, Count());
        }
    }
}
