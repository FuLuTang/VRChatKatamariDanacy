using NUnit.Framework;
using UnityEngine;
using VRCKatamari.BallLayers;

namespace VRCKatamari.Tests
{
    public class AttachmentStepTests
    {
        private string[] ids;
        private Vector3[] positions;
        private Vector3[] localPositions;
        private Quaternion[] localRotations;
        private Vector3[] centers;
        private float[] radii;
        private float[] margins;
        private int[] steps;
        private bool[] valid;
        private int eventCount;
        private int ringCount;
        private Vector3[] directions;
        private float[] supports;
        private Vector3[] pool;
        private int endpointCount;
        private int[] itemStarts;
        private int[] itemLengths;
        private string[] itemKeys;
        private bool[] isCandidate;
        private int itemCount;
        private string[] candidateKeys;
        private int candidateCount;
        private int[] affected;
        private float[] scratch;
        private Vector3[] corners;
        private int skipReason;

        [SetUp]
        public void SetUp()
        {
            int capacity = AttachmentStepMath.EventCapacity;
            ids = new string[capacity];
            positions = new Vector3[capacity];
            localPositions = new Vector3[capacity];
            localRotations = new Quaternion[capacity];
            centers = new Vector3[capacity];
            radii = new float[capacity];
            margins = new float[capacity];
            steps = new int[capacity];
            valid = new bool[capacity];
            eventCount = ringCount = endpointCount = itemCount = candidateCount = 0;
            directions = new Vector3[RadialProxyMath.DirectionCount];
            Assert.IsTrue(RadialProxyMath.TryFillDirections(directions));
            supports = new float[RadialProxyMath.DirectionCount];
            pool = new Vector3[AttachmentStepMath.ProxyEndpointCapacity];
            itemStarts = new int[AttachmentStepMath.ProxyItemCapacity];
            itemLengths = new int[AttachmentStepMath.ProxyItemCapacity];
            itemKeys = new string[AttachmentStepMath.ProxyItemCapacity];
            isCandidate = new bool[AttachmentStepMath.ProxyItemCapacity];
            candidateKeys = new string[RadialProxyMath.MaxExtremeCandidates];
            affected = new int[RadialProxyMath.DirectionCount];
            scratch = new float[RadialProxyMath.DirectionCount];
            corners = new Vector3[RadialProxyShellMath.AabbCornerCount];
        }

        private void Record(string id, Quaternion rotation, Vector3 previous, Vector3 contact,
            Vector3 position, float radius, int step)
        {
            AttachmentStepMath.RecordAbsorbedEvent(id, position, rotation, previous,
                Quaternion.identity, contact, Vector3.up, radius, radius, .005f, false,
                1f, step, ids, positions, localPositions, localRotations, centers, radii,
                margins, steps, valid, ref eventCount, ref ringCount);
        }

        private bool Read(int index, int now, out Vector3 position, out Quaternion rotation,
            out Vector3 center, out float radius, out float margin, out int delay)
        {
            return AttachmentStepMath.TryReadAbsorbedEventAttachment(index, now, ids,
                localPositions, localRotations, centers, radii, margins, steps, valid,
                eventCount, ringCount, out position, out rotation, out center, out radius,
                out margin, out delay);
        }

        private bool Attach(string id, Vector3 size, Vector3 localPosition,
            Quaternion localRotation, float core, float minProtrusion)
        {
            return AttachmentStepMath.TryAttachRadialProxy(id, size, localPosition,
                localRotation, core, minProtrusion, directions, supports, pool,
                ref endpointCount, itemStarts, itemLengths, itemKeys, isCandidate,
                ref itemCount, candidateKeys, ref candidateCount, affected, scratch,
                corners, out skipReason);
        }

        [TestCase(0)]
        [TestCase(8)]
        public void EventReadReturnsPreGrowthContactRollAndDimensions(int laterSteps)
        {
            Vector3 contact = new Vector3(Mathf.PI * .1f / 2f, 0f, 0f);
            Record("item", Quaternion.identity, Vector3.zero, contact,
                contact + new Vector3(.12f, 0f, 0f), .1f, 10);
            Vector3 position, center;
            Quaternion rotation;
            float radius, margin;
            int delay;
            Assert.IsTrue(Read(0, 10 + laterSteps, out position, out rotation, out center,
                out radius, out margin, out delay));
            Assert.Less(Vector3.Distance(new Vector3(0f, .12f, 0f), position), 1e-6f);
            Assert.AreEqual(System.Math.Sqrt(.5), rotation.z, 1e-6);
            Assert.AreEqual(System.Math.Sqrt(.5), rotation.w, 1e-6);
            Assert.AreEqual(contact, center);
            Assert.AreEqual(.1f, radius);
            Assert.AreEqual(.005f, margin);
            Assert.AreEqual(laterSteps, delay);
            Assert.AreEqual(1, eventCount);
            Assert.AreEqual(1, ringCount);
        }

        [Test]
        public void FullRingEvictsOldestAndResetOccupancyDoesNotReplayOldEvents()
        {
            for (int i = 0; i <= AttachmentStepMath.EventCapacity; i++)
                Record("item-" + i, Quaternion.identity, Vector3.zero, Vector3.zero,
                    new Vector3(i, 0f, 0f), .1f, i);
            Assert.AreEqual(AttachmentStepMath.EventCapacity + 1, eventCount);
            Assert.AreEqual(AttachmentStepMath.EventCapacity, ringCount);
            Assert.AreEqual(-1, AttachmentStepMath.AbsorbedEventSlot(0, ids, eventCount, ringCount));
            Assert.AreEqual(-1, AttachmentStepMath.AbsorbedEventSlot(-1, ids, eventCount, ringCount));
            Assert.AreEqual(-1, AttachmentStepMath.AbsorbedEventSlot(eventCount, ids, eventCount, ringCount));
            Vector3 position, center;
            Quaternion rotation;
            float radius, margin;
            int delay;
            Assert.IsFalse(Read(0, 20, out position, out rotation, out center, out radius,
                out margin, out delay));
            Assert.IsTrue(Read(16, 20, out position, out rotation, out center, out radius,
                out margin, out delay));
            Assert.AreEqual(new Vector3(16f, 0f, 0f), position);
            Assert.AreEqual(4, delay);
            ringCount = 0;
            Assert.IsFalse(Read(16, 20, out position, out rotation, out center, out radius,
                out margin, out delay));
            Record("new-round", Quaternion.identity, Vector3.zero, Vector3.zero,
                Vector3.one, .2f, 20);
            Assert.AreEqual(18, eventCount);
            Assert.IsTrue(Read(17, 20, out position, out rotation, out center, out radius,
                out margin, out delay));
            Assert.AreEqual(Vector3.one, position);
        }

        [Test]
        public void InvalidPoseIsRecordedButCannotFallbackToCurrentPose()
        {
            Record("invalid", new Quaternion(0f, 0f, 0f, 0f), Vector3.zero,
                Vector3.zero, Vector3.one, .1f, 10);
            Vector3 position, center;
            Quaternion rotation;
            float radius, margin;
            int delay;
            Assert.AreEqual(1, eventCount);
            Assert.IsFalse(Read(0, 11, out position, out rotation, out center, out radius,
                out margin, out delay));
            Assert.AreEqual(Vector3.zero, position);
            Assert.AreEqual(Quaternion.identity, rotation);
            Assert.AreEqual(0, delay);
        }

        [TestCase(0f)]
        [TestCase(.01f)]
        public void ZeroOrThresholdProtrusionDoesNotRegister(float minProtrusion)
        {
            Assert.IsFalse(Attach("inside", Vector3.zero, new Vector3(.1f + minProtrusion, 0f, 0f),
                Quaternion.identity, .1f, minProtrusion));
            Assert.AreEqual(AttachmentStepMath.ProxySkipInsideCore, skipReason);
            Assert.AreEqual(0, endpointCount);
            Assert.AreEqual(0, itemCount);
            CollectionAssert.AreEqual(new float[RadialProxyMath.DirectionCount], supports);
        }

        [TestCase(-1f)]
        [TestCase(float.NaN)]
        [TestCase(float.PositiveInfinity)]
        public void InvalidMinProtrusionFallsBackToZero(float minProtrusion)
        {
            Assert.IsTrue(Attach("item", Vector3.zero, new Vector3(.1001f, 0f, 0f),
                Quaternion.identity, .1f, minProtrusion));
            Assert.AreEqual(AttachmentStepMath.ProxySkipNone, skipReason);
            Assert.AreEqual(8, endpointCount);
            Assert.AreEqual(1, itemCount);
            Assert.AreEqual(0, candidateCount);
        }

        [Test]
        public void ProxyCapacityIs256AndDuplicateIsIdempotentEvenWhenFull()
        {
            for (int i = 0; i < AttachmentStepMath.ProxyItemCapacity; i++)
                Assert.IsTrue(Attach("item-" + i, Vector3.zero, Vector3.right,
                    Quaternion.identity, .1f, .01f));
            Assert.AreEqual(256, itemCount);
            Assert.AreEqual(2048, endpointCount);
            float[] before = (float[])supports.Clone();
            Assert.IsFalse(Attach("overflow", Vector3.zero, Vector3.right,
                Quaternion.identity, .1f, .01f));
            Assert.AreEqual(AttachmentStepMath.ProxySkipPoolFull, skipReason);
            Assert.IsFalse(Attach("inside-full", Vector3.zero, Vector3.zero,
                Quaternion.identity, .1f, .01f));
            Assert.AreEqual(AttachmentStepMath.ProxySkipInsideCore, skipReason);
            Assert.IsTrue(Attach("item-0", new Vector3(float.NaN, 0f, 0f),
                new Vector3(float.NaN, 0f, 0f), new Quaternion(0f, 0f, 0f, 0f), float.NaN, .01f));
            Assert.AreEqual(AttachmentStepMath.ProxySkipNone, skipReason);
            Assert.AreEqual(256, itemCount);
            Assert.AreEqual(2048, endpointCount);
            CollectionAssert.AreEqual(before, supports);
        }

        [Test]
        public void EndpointCapacityAndShellRefusalHaveDistinctReasons()
        {
            endpointCount = AttachmentStepMath.ProxyEndpointCapacity - 7;
            Assert.IsFalse(Attach("overflow", Vector3.zero, Vector3.right,
                Quaternion.identity, .1f, 0f));
            Assert.AreEqual(AttachmentStepMath.ProxySkipPoolFull, skipReason);
            Assert.AreEqual(0, itemCount);
            endpointCount = 0;
            pool = new Vector3[7];
            Assert.IsFalse(Attach("short-buffer", Vector3.zero, Vector3.right,
                Quaternion.identity, .1f, 0f));
            Assert.AreEqual(AttachmentStepMath.ProxySkipShellRefused, skipReason);
            Assert.AreEqual(0, endpointCount);
            Assert.AreEqual(0, itemCount);
        }

        [Test]
        public void TurnedBoxContributesItsEightBallLocalCornersToAllDirections()
        {
            Quaternion quarterTurn = new Quaternion(0f, 0f, (float)System.Math.Sqrt(.5),
                (float)System.Math.Sqrt(.5));
            Assert.IsTrue(Attach("box", new Vector3(.4f, .2f, .1f), Vector3.right,
                quarterTurn, .1f, .01f));
            Assert.AreEqual(8, endpointCount);
            Assert.AreEqual(0, candidateCount);
            for (int d = 0; d < directions.Length; d++)
            {
                float expected = 0f;
                for (int c = 0; c < 8; c++)
                    expected = System.Math.Max(expected, Vector3.Dot(directions[d], pool[c]));
                Assert.AreEqual(expected, supports[d], 1e-6f);
            }
            Assert.AreEqual(1.1f, pool[0].x, 1e-6f);
            Assert.AreEqual(-.2f, pool[0].y, 1e-6f);
            Assert.AreEqual(-.05f, pool[0].z, 1e-6f);
        }

        [Test]
        public void InvalidNewItemFailsBeforeAnyShellMutation()
        {
            Assert.IsFalse(Attach("bad", Vector3.one, Vector3.zero,
                new Quaternion(0f, 0f, 0f, 0f), .1f, .01f));
            Assert.AreEqual(AttachmentStepMath.ProxySkipInvalidInput, skipReason);
            Assert.IsFalse(Attach("", Vector3.one, Vector3.zero,
                Quaternion.identity, .1f, .01f));
            Assert.AreEqual(AttachmentStepMath.ProxySkipInvalidInput, skipReason);
            Assert.AreEqual(0, itemCount);
            Assert.AreEqual(0, endpointCount);
        }
    }
}
