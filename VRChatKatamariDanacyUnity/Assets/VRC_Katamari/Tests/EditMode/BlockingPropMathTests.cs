using NUnit.Framework;
using UnityEngine;
using VRCKatamari.Pickup;

namespace VRCKatamari.Tests
{
    /// <summary>
    /// EditMode port of Docs/tests/pickup_absorb/test_blocking_props.py, the same ten
    /// cases as BLOCKING_PROPS_PLAN.md §6 (1-6, 9, 10 plus the 7/8 limit-edge cases).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every expected value is a hand calculation or a geometric invariant; no
    /// assertion re-runs the implementation formula. Lengths are metres in world
    /// space, boxes are min/max corners, and the helpers' extents argument is the
    /// item's FULL size (this project's bounds convention), so a box is
    /// `centre ± extents / 2`.
    /// </para>
    /// <para>
    /// The Python cases use doubles; this port uses floats, so only values that are
    /// exact in binary are compared exactly (1.5, 1.75, 1.25, 1.0, 0/2), and the
    /// deliberately "just past" values (0.5001, 1.0001, 0.9999) stay far above float
    /// noise. Do not tighten them to one ulp: the runtime comparison has no epsilon.
    /// </para>
    /// </remarks>
    public class BlockingPropMathTests
    {
        private const float Tolerance = 1e-5f;

        // Far above every ball radius used here, so the growth-cap gate cannot mask the
        // size gate in the cases that call the absorb gate.
        private const float MaxGrowthRadiusM = 5f;

        private static readonly string[] Allowed = new string[] { "stone" };

        private static int Select(Vector3 ballCentre, float radiusM, Vector3[] minima,
            Vector3[] maxima, bool[] candidate, int count, int[] previousIndices,
            int previousCount, float stickinessM, int[] outIndices, float[] outDistances,
            int capacity)
        {
            return BlockingPropMath.SelectNearest(ballCentre, radiusM, minima, maxima,
                candidate, count, previousIndices, previousCount, stickinessM, outIndices,
                outDistances, capacity);
        }

        [Test]
        public void NearFaceOfALongPropBeatsTheCentreOfASmallOne()
        {
            // The 3 m bench's centre is 2 m away but its near face is 1.5 m away; the
            // small prop's centre is 1.875 m away and its near face 1.75 m away.
            // Centre-distance ranking would pick the small prop, AABB distance the bench.
            Vector3 benchMin;
            Vector3 benchMax;
            Assert.IsTrue(BlockingPropMath.TryBoxFromCentreAndExtents(
                new Vector3(0f, 0f, 2f), new Vector3(3f, 0.5f, 1f),
                out benchMin, out benchMax));
            Assert.AreEqual(-1.5f, benchMin.x, 0f);
            Assert.AreEqual(-0.25f, benchMin.y, 0f);
            Assert.AreEqual(1.5f, benchMin.z, 0f);
            Assert.AreEqual(1.5f, benchMax.x, 0f);
            Assert.AreEqual(0.25f, benchMax.y, 0f);
            Assert.AreEqual(2.5f, benchMax.z, 0f);

            Vector3 smallMin;
            Vector3 smallMax;
            Assert.IsTrue(BlockingPropMath.TryBoxFromCentreAndExtents(
                new Vector3(0f, 0f, 1.875f), new Vector3(0.25f, 0.25f, 0.25f),
                out smallMin, out smallMax));
            Assert.Less(1.875f, 2f,
                "the small prop must be nearer by centre distance for this case to mean something");

            int[] indices = new int[2];
            float[] distances = new float[2];
            int written = Select(Vector3.zero, 4f,
                new Vector3[] { benchMin, smallMin }, new Vector3[] { benchMax, smallMax },
                new bool[] { true, true }, 2, null, 0, 0f, indices, distances, 2);
            Assert.AreEqual(2, written);
            Assert.AreEqual(0, indices[0]);
            Assert.AreEqual(1, indices[1]);
            Assert.AreEqual(1.5f, distances[0], 0f);
            Assert.AreEqual(1.75f, distances[1], 0f);
        }

        [Test]
        public void BallInsideABoxIsDistanceZeroAndRanksFirst()
        {
            Vector3 insideMin = new Vector3(-1f, -1f, -1f);
            Vector3 insideMax = new Vector3(1f, 1f, 1f);
            Vector3 nearMin = new Vector3(2f, -1f, -1f);
            Vector3 nearMax = new Vector3(3f, 1f, 1f);
            Vector3 ball = new Vector3(0f, 0f, 0.5f);

            float distance;
            Assert.IsTrue(BlockingPropMath.TryDistanceToBox(ball, insideMin, insideMax,
                out distance));
            Assert.AreEqual(0f, distance, 0f);
            Assert.IsTrue(BlockingPropMath.TryDistanceToBox(ball, nearMin, nearMax,
                out distance));
            Assert.AreEqual(2f, distance, 0f);

            int[] indices = new int[2];
            float[] distances = new float[2];
            int written = Select(ball, 3f,
                new Vector3[] { insideMin, nearMin }, new Vector3[] { insideMax, nearMax },
                new bool[] { true, true }, 2, null, 0, 0f, indices, distances, 2);
            Assert.AreEqual(2, written);
            Assert.AreEqual(0, indices[0]);
            Assert.AreEqual(0f, distances[0], 0f);
            Assert.AreEqual(1, indices[1]);
            Assert.AreEqual(2f, distances[1], 0f);
        }

        [Test]
        public void EqualDistancesBreakByAscendingIndexAndAreStable()
        {
            Vector3 frontMin = new Vector3(-0.5f, -0.5f, 1.5f);
            Vector3 frontMax = new Vector3(0.5f, 0.5f, 2.5f);
            Vector3 backMin = new Vector3(-0.5f, -0.5f, -2.5f);
            Vector3 backMax = new Vector3(0.5f, 0.5f, -1.5f);

            int[] firstIndices = new int[2];
            float[] firstDistances = new float[2];
            int first = Select(Vector3.zero, 4f,
                new Vector3[] { frontMin, backMin }, new Vector3[] { frontMax, backMax },
                new bool[] { true, true }, 2, null, 0, 0f, firstIndices, firstDistances, 2);
            int[] secondIndices = new int[2];
            float[] secondDistances = new float[2];
            int second = Select(Vector3.zero, 4f,
                new Vector3[] { frontMin, backMin }, new Vector3[] { frontMax, backMax },
                new bool[] { true, true }, 2, null, 0, 0f, secondIndices, secondDistances, 2);

            // Both boxes are 1.5 m away, so the smaller index wins in both calls.
            Assert.AreEqual(2, first);
            Assert.AreEqual(2, second);
            Assert.AreEqual(0, firstIndices[0]);
            Assert.AreEqual(1, firstIndices[1]);
            Assert.AreEqual(1.5f, firstDistances[0], 0f);
            Assert.AreEqual(1.5f, firstDistances[1], 0f);
            Assert.AreEqual(firstIndices[0], secondIndices[0]);
            Assert.AreEqual(firstIndices[1], secondIndices[1]);
            Assert.AreEqual(firstDistances[0], secondDistances[0], 0f);
            Assert.AreEqual(firstDistances[1], secondDistances[1], 0f);

            int[] clippedIndices = new int[1];
            float[] clippedDistances = new float[1];
            Assert.AreEqual(1, Select(Vector3.zero, 4f,
                new Vector3[] { frontMin, backMin }, new Vector3[] { frontMax, backMax },
                new bool[] { true, true }, 2, null, 0, 0f, clippedIndices,
                clippedDistances, 1));
            Assert.AreEqual(0, clippedIndices[0]);
        }

        [Test]
        public void StickinessKeepsAPreviousPropUntilTheNewOneLeavesTheStickyBand()
        {
            // The old prop is at 1.2 m: outside the 1 m radius but inside the 1.25 m
            // sticky band, so it keeps its slot; the new prop is nearer at 1.0 m.
            Vector3 oldMin = new Vector3(1.2f, -0.5f, -0.5f);
            Vector3 oldMax = new Vector3(1.6f, 0.5f, 0.5f);
            Vector3 newMin = new Vector3(1f, -0.5f, -0.5f);
            Vector3 newMax = new Vector3(1.4f, 0.5f, 0.5f);

            int[] keptIndices = new int[1];
            float[] keptDistances = new float[1];
            Assert.AreEqual(1, Select(Vector3.zero, 1f,
                new Vector3[] { oldMin, newMin }, new Vector3[] { oldMax, newMax },
                new bool[] { true, true }, 2, new int[] { 0 }, 1, 0.25f,
                keptIndices, keptDistances, 1));
            Assert.AreEqual(0, keptIndices[0]);
            Assert.AreEqual(1.2f, keptDistances[0], Tolerance);

            // At 1.26 m the old prop is past radius + stickiness, so the nearer new one
            // takes the only slot.
            Vector3 movedMin = new Vector3(1.26f, -0.5f, -0.5f);
            Vector3 movedMax = new Vector3(1.66f, 0.5f, 0.5f);
            int[] swappedIndices = new int[1];
            float[] swappedDistances = new float[1];
            Assert.AreEqual(1, Select(Vector3.zero, 1f,
                new Vector3[] { movedMin, newMin }, new Vector3[] { movedMax, newMax },
                new bool[] { true, true }, 2, new int[] { 0 }, 1, 0.25f,
                swappedIndices, swappedDistances, 1));
            Assert.AreEqual(1, swappedIndices[0]);
            Assert.AreEqual(1f, swappedDistances[0], 0f);

            // With no previous batch there is no stickiness at all: the nearer prop wins.
            int[] freshIndices = new int[1];
            float[] freshDistances = new float[1];
            Assert.AreEqual(1, Select(Vector3.zero, 1f,
                new Vector3[] { oldMin, newMin }, new Vector3[] { oldMax, newMax },
                new bool[] { true, true }, 2, null, 0, 0.25f, freshIndices,
                freshDistances, 1));
            Assert.AreEqual(1, freshIndices[0]);
        }

        [Test]
        public void CapacityClampAndEmptyInputs()
        {
            Vector3[] minima = new Vector3[]
            {
                new Vector3(3f, -0.5f, -0.5f),
                new Vector3(1f, -0.5f, -0.5f),
                new Vector3(2f, -0.5f, -0.5f)
            };
            Vector3[] maxima = new Vector3[]
            {
                new Vector3(3.6f, 0.5f, 0.5f),
                new Vector3(1.6f, 0.5f, 0.5f),
                new Vector3(2.6f, 0.5f, 0.5f)
            };

            int[] indices = new int[2];
            float[] distances = new float[2];
            Assert.AreEqual(2, Select(Vector3.zero, 10f, minima, maxima,
                new bool[] { true, true, true }, 3, null, 0, 0f, indices, distances, 2));
            Assert.AreEqual(1, indices[0]);
            Assert.AreEqual(2, indices[1]);
            Assert.AreEqual(1f, distances[0], 0f);
            Assert.AreEqual(2f, distances[1], 0f);

            // capacity 0 is legal: nothing is written, not even a sentinel.
            int[] zeroIndices = new int[] { -7, -7 };
            float[] zeroDistances = new float[] { -7f, -7f };
            Assert.AreEqual(0, Select(Vector3.zero, 10f, minima, maxima,
                new bool[] { true, true, true }, 3, null, 0, 0f, zeroIndices,
                zeroDistances, 0));
            Assert.AreEqual(-7, zeroIndices[0]);
            Assert.AreEqual(-7f, zeroDistances[0], 0f);

            // count 0 is legal too, and null input arrays are allowed in that case.
            Assert.AreEqual(0, Select(Vector3.zero, 10f, null, null, null, 0, null, 0, 0f,
                indices, distances, 2));

            // A candidate flag of false keeps an otherwise nearer prop out.
            int[] skippedIndices = new int[2];
            float[] skippedDistances = new float[2];
            Assert.AreEqual(2, Select(Vector3.zero, 10f, minima, maxima,
                new bool[] { true, false, true }, 3, null, 0, 0f, skippedIndices,
                skippedDistances, 2));
            Assert.AreEqual(2, skippedIndices[0]);
            Assert.AreEqual(0, skippedIndices[1]);
            Assert.AreEqual(2f, skippedDistances[0], 0f);
            Assert.AreEqual(3f, skippedDistances[1], 0f);
        }

        [Test]
        public void NonFiniteOrInvertedInputIsRefused()
        {
            Vector3 goodMin = new Vector3(-1f, -1f, -1f);
            Vector3 goodMax = new Vector3(1f, 1f, 1f);

            float distance;
            Assert.IsFalse(BlockingPropMath.TryDistanceToBox(new Vector3(float.NaN, 0f, 0f),
                goodMin, goodMax, out distance));
            Assert.AreEqual(0f, distance, 0f);
            // min > max is an inverted box, a data error rather than a flat box.
            Assert.IsFalse(BlockingPropMath.TryDistanceToBox(Vector3.zero,
                new Vector3(1f, -1f, -1f), new Vector3(0f, 1f, 1f), out distance));

            Vector3 minimum;
            Vector3 maximum;
            Assert.IsFalse(BlockingPropMath.TryBoxFromCentreAndExtents(Vector3.zero,
                new Vector3(-1f, 1f, 1f), out minimum, out maximum));
            Assert.AreEqual(Vector3.zero, minimum);
            Assert.AreEqual(Vector3.zero, maximum);
            Assert.IsFalse(BlockingPropMath.TryBoxFromCentreAndExtents(
                new Vector3(float.PositiveInfinity, 0f, 0f), Vector3.one,
                out minimum, out maximum));

            int[] indices = new int[] { -7, -7 };
            float[] distances = new float[] { -7f, -7f };
            Vector3[] minima = new Vector3[] { new Vector3(float.NaN, -0.5f, -0.5f) };
            Vector3[] maxima = new Vector3[] { new Vector3(0.5f, 0.5f, 0.5f) };
            bool[] candidate = new bool[] { true };
            // A non-finite ball centre, a bad box, a negative radius, a count past the
            // arrays, a previous count past its array: each refuses and writes nothing.
            Assert.AreEqual(-1, BlockingPropMath.SelectNearest(
                new Vector3(float.NaN, 0f, 0f), 1f, minima, maxima, candidate, 1,
                null, 0, 0f, indices, distances, 1));
            Assert.AreEqual(-1, Select(Vector3.zero, 1f, minima, maxima, candidate, 1,
                null, 0, 0f, indices, distances, 1));
            Assert.AreEqual(-1, Select(Vector3.zero, -1f, new Vector3[] { goodMin },
                new Vector3[] { goodMax }, candidate, 1, null, 0, 0f, indices, distances, 1));
            Assert.AreEqual(-1, Select(Vector3.zero, 1f, new Vector3[] { goodMin },
                new Vector3[] { goodMax }, candidate, 2, null, 0, 0f, indices, distances, 1));
            Assert.AreEqual(-1, Select(Vector3.zero, 1f, new Vector3[] { goodMin },
                new Vector3[] { goodMax }, candidate, 1, new int[1], 2, 0f, indices,
                distances, 1));
            // capacity past the output buffers.
            Assert.AreEqual(-1, Select(Vector3.zero, 1f, new Vector3[] { goodMin },
                new Vector3[] { goodMax }, candidate, 1, null, 0, 0f, indices, distances, 3));
            Assert.AreEqual(-7, indices[0]);
            Assert.AreEqual(-7f, distances[0], 0f);
            Assert.AreEqual(-7, indices[1]);
            Assert.AreEqual(-7f, distances[1], 0f);
        }

        [Test]
        public void ExtentEqualToTheLimitIsAbsorbable()
        {
            // Equality is inclusive: extent == limit is absorbable and does not block;
            // only a strictly larger extent does.
            Assert.IsFalse(PickupAbsorbMath.IsBeyondAbsorbLimit(
                new Vector3(0.5f, 0.05f, 0.05f), 0.5f));
            Assert.IsFalse(PickupAbsorbMath.IsBeyondAbsorbLimit(
                new Vector3(0.4999f, 0.05f, 0.05f), 0.5f));
            Assert.IsTrue(PickupAbsorbMath.IsBeyondAbsorbLimit(
                new Vector3(0.5001f, 0.05f, 0.05f), 0.5f));

            // The compared quantity is the largest full extent, not the first component.
            Assert.IsFalse(PickupAbsorbMath.IsBeyondAbsorbLimit(
                new Vector3(0.1f, 0.5f, 0.1f), 0.5f));
            Assert.IsTrue(PickupAbsorbMath.IsBeyondAbsorbLimit(
                new Vector3(0.1f, 0.5001f, 0.1f), 0.5f));

            // Bad data counts as beyond: a NaN comparison is never true, and letting it
            // through would leave the prop both unabsorbable and non-blocking.
            Assert.IsTrue(PickupAbsorbMath.IsBeyondAbsorbLimit(
                new Vector3(float.NaN, 0f, 0f), 0.5f));
            Assert.IsTrue(PickupAbsorbMath.IsBeyondAbsorbLimit(
                new Vector3(-1f, 0.05f, 0.05f), 0.5f));
            Assert.IsTrue(PickupAbsorbMath.IsBeyondAbsorbLimit(
                new Vector3(0.5f, 0.05f, 0.05f), float.PositiveInfinity));
        }

        [Test]
        public void GrowthCrossingTheLimitFlipsTheSameFunction()
        {
            Vector3 bounds = new Vector3(1.25f, 0.05f, 0.05f);
            float limit;

            // A 1.20 m ball resolves a 1.20 m limit, so the 1.25 m prop blocks and the
            // gate refuses it as too large.
            Assert.IsTrue(PickupAbsorbMath.TryComputeAbsorbSizeLimit(1.20f, 1f, 8f, out limit));
            Assert.AreEqual(1.20f, limit, Tolerance);
            Assert.IsTrue(PickupAbsorbMath.IsBeyondAbsorbLimit(bounds, limit));
            AbsorbOutcome smallBall = PickupAbsorbMath.EvaluateAbsorbWithBallSize(true,
                "stone", Allowed, Allowed.Length, bounds, Vector3.zero, Vector3.zero,
                1.20f * 0.5f, 0f, 8f, MaxGrowthRadiusM, 1.20f, 1f);
            Assert.AreEqual(AbsorbOutcome.RejectItemTooLarge, smallBall);

            // Growing to 1.25 m raises the limit to 1.25 m: the same prop becomes
            // absorbable, and the host-side predicate agrees with the gate.
            Assert.IsTrue(PickupAbsorbMath.TryComputeAbsorbSizeLimit(1.25f, 1f, 8f, out limit));
            Assert.AreEqual(1.25f, limit, 0f);
            Assert.IsFalse(PickupAbsorbMath.IsBeyondAbsorbLimit(bounds, limit));
            AbsorbOutcome bigBall = PickupAbsorbMath.EvaluateAbsorbWithBallSize(true,
                "stone", Allowed, Allowed.Length, bounds, Vector3.zero, Vector3.zero,
                1.25f * 0.5f, 0f, 8f, MaxGrowthRadiusM, 1.25f, 1f);
            Assert.AreEqual(AbsorbOutcome.Absorb, bigBall);

            Assert.AreEqual(smallBall == AbsorbOutcome.RejectItemTooLarge,
                PickupAbsorbMath.IsBeyondAbsorbLimit(bounds, 1.20f));
            Assert.AreEqual(bigBall == AbsorbOutcome.RejectItemTooLarge,
                PickupAbsorbMath.IsBeyondAbsorbLimit(bounds, 1.25f));
        }

        [Test]
        public void ShrinkingRecomputesWithNoResidualState()
        {
            Vector3 bench = new Vector3(1.25f, 0.1f, 0.1f);
            Vector3 pebble = new Vector3(0.3f, 0.1f, 0.1f);

            // Same inputs, different limits: the answer only depends on the arguments,
            // because nothing is cached.
            Assert.IsFalse(PickupAbsorbMath.IsBeyondAbsorbLimit(bench, 1.25f));
            Assert.IsFalse(PickupAbsorbMath.IsBeyondAbsorbLimit(pebble, 1.25f));
            Assert.IsTrue(PickupAbsorbMath.IsBeyondAbsorbLimit(bench, 1.20f));
            Assert.IsFalse(PickupAbsorbMath.IsBeyondAbsorbLimit(pebble, 1.20f));
            Assert.IsFalse(PickupAbsorbMath.IsBeyondAbsorbLimit(bench, 1.25f));

            Vector3[] minima = new Vector3[]
            {
                new Vector3(1f, -0.5f, -0.5f),
                new Vector3(2f, -0.5f, -0.5f)
            };
            Vector3[] maxima = new Vector3[]
            {
                new Vector3(1.5f, 0.5f, 0.5f),
                new Vector3(2.5f, 0.5f, 0.5f)
            };
            bool[] grown = new bool[] { false, false };
            bool[] shrunk = new bool[]
            {
                PickupAbsorbMath.IsBeyondAbsorbLimit(bench, 1.20f),
                PickupAbsorbMath.IsBeyondAbsorbLimit(pebble, 1.20f)
            };
            int[] indices = new int[2];
            float[] distances = new float[2];

            Assert.AreEqual(0, Select(Vector3.zero, 10f, minima, maxima, grown, 2,
                null, 0, 0f, indices, distances, 2));
            Assert.AreEqual(1, Select(Vector3.zero, 10f, minima, maxima, shrunk, 2,
                null, 0, 0f, indices, distances, 2));
            Assert.AreEqual(0, indices[0]);
            Assert.AreEqual(1f, distances[0], 0f);
            Assert.AreEqual(0, Select(Vector3.zero, 10f, minima, maxima, grown, 2,
                null, 0, 0f, indices, distances, 2));
        }

        [Test]
        public void OutsideTheRadiusIsNotSelectedAndTheBoundaryIsInclusive()
        {
            Vector3 onEdgeMin = new Vector3(1f, -0.5f, -0.5f);
            Vector3 onEdgeMax = new Vector3(2f, 0.5f, 0.5f);
            Vector3 outsideMin = new Vector3(1.0001f, -0.5f, -0.5f);
            Vector3 outsideMax = new Vector3(2.0001f, 0.5f, 0.5f);
            Vector3 insideMin = new Vector3(0.9999f, -0.5f, -0.5f);
            Vector3 insideMax = new Vector3(1.9999f, 0.5f, 0.5f);

            float distance;
            Assert.IsTrue(BlockingPropMath.TryDistanceToBox(Vector3.zero, onEdgeMin,
                onEdgeMax, out distance));
            Assert.AreEqual(1f, distance, 0f);

            int[] indices = new int[1];
            float[] distances = new float[1];
            // Exactly at the radius is inside (inclusive, like the absorb reach).
            Assert.AreEqual(1, Select(Vector3.zero, 1f, new Vector3[] { onEdgeMin },
                new Vector3[] { onEdgeMax }, new bool[] { true }, 1, null, 0, 0f,
                indices, distances, 1));
            Assert.AreEqual(0, indices[0]);
            Assert.AreEqual(1f, distances[0], 0f);
            // Just past it is not selected at all.
            Assert.AreEqual(0, Select(Vector3.zero, 1f, new Vector3[] { outsideMin },
                new Vector3[] { outsideMax }, new bool[] { true }, 1, null, 0, 0f,
                indices, distances, 1));
            // Just inside is selected.
            Assert.AreEqual(1, Select(Vector3.zero, 1f, new Vector3[] { insideMin },
                new Vector3[] { insideMax }, new bool[] { true }, 1, null, 0, 0f,
                indices, distances, 1));
            Assert.AreEqual(0, indices[0]);
            Assert.AreEqual(0.9999f, distances[0], Tolerance);
        }
    }
}
