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
        public void VolumeEqualToTheLimitIsAbsorbable()
        {
            // Equality is inclusive: item volume == limit is absorbable and does not
            // block; only a strictly larger volume does. 1 * 0.5 * 1 = 0.5 m^3 is exact in
            // binary, so the equality case is real and not 1 ulp away.
            Assert.IsFalse(PickupAbsorbMath.IsBeyondAbsorbVolumeLimit(
                new Vector3(1f, 0.5f, 1f), 0.5f));
            Assert.IsFalse(PickupAbsorbMath.IsBeyondAbsorbVolumeLimit(
                new Vector3(0.9999f, 0.5f, 1f), 0.5f));
            Assert.IsTrue(PickupAbsorbMath.IsBeyondAbsorbVolumeLimit(
                new Vector3(1.0001f, 0.5f, 1f), 0.5f));

            // The compared quantity is the AABB volume, not one axis and not the longest
            // edge. A 0.85 m bat (0.02 x 0.02) is 0.00034 m^3, far inside a 0.5 m^3 limit
            // even though its longest edge is longer than that limit would allow if the
            // limit were a length; 0.9 x 0.9 x 0.6 = 0.486 m^3 is inside, and a 0.9 m cube
            // (0.729 m^3) is beyond.
            Assert.IsFalse(PickupAbsorbMath.IsBeyondAbsorbVolumeLimit(
                new Vector3(0.02f, 0.02f, 0.85f), 0.5f));
            Assert.IsFalse(PickupAbsorbMath.IsBeyondAbsorbVolumeLimit(
                new Vector3(0.9f, 0.9f, 0.6f), 0.5f));
            Assert.IsTrue(PickupAbsorbMath.IsBeyondAbsorbVolumeLimit(
                new Vector3(0.9f, 0.9f, 0.9f), 0.5f));

            // Bad data counts as beyond: a NaN comparison is never true, and letting it
            // through would leave the prop both unabsorbable and non-blocking.
            Assert.IsTrue(PickupAbsorbMath.IsBeyondAbsorbVolumeLimit(
                new Vector3(float.NaN, 0f, 0f), 0.5f));
            Assert.IsTrue(PickupAbsorbMath.IsBeyondAbsorbVolumeLimit(
                new Vector3(-1f, 0.05f, 0.05f), 0.5f));
            Assert.IsTrue(PickupAbsorbMath.IsBeyondAbsorbVolumeLimit(
                new Vector3(0.5f, 0.05f, 0.05f), float.PositiveInfinity));
            // A volume that overflows float is bad data too, not "infinitely big".
            Assert.IsTrue(PickupAbsorbMath.IsBeyondAbsorbVolumeLimit(
                new Vector3(1e20f, 1e20f, 1e20f), 0.5f));
        }

        [Test]
        public void GrowthCrossingTheLimitFlipsTheSameFunction()
        {
            // A 1.25 x 0.05 x 0.05 m plank is 0.003125 m^3.
            Vector3 bounds = new Vector3(1.25f, 0.05f, 0.05f);
            float limit;

            // A 0.30 m ball resolves (pi/6) * 0.027 / 10 = 0.001414 m^3, so the plank
            // blocks and the gate refuses it as too large.
            Assert.IsTrue(PickupAbsorbMath.TryComputeAbsorbVolumeLimit(0.30f, 10f, 512f,
                out limit));
            Assert.AreEqual(0.001414f, limit, 1e-6f);
            Assert.IsTrue(PickupAbsorbMath.IsBeyondAbsorbVolumeLimit(bounds, limit));
            AbsorbOutcome smallBall = PickupAbsorbMath.EvaluateAbsorbWithBallSize(true,
                "stone", Allowed, Allowed.Length, bounds, Vector3.zero, Vector3.zero,
                0.30f * 0.5f, 0f, 512f, MaxGrowthRadiusM, 0.30f, 10f);
            Assert.AreEqual(AbsorbOutcome.RejectItemTooLarge, smallBall);

            // Growing to 0.50 m raises the limit to (pi/6) * 0.125 / 10 = 0.006545 m^3:
            // the same plank becomes absorbable, and the host-side predicate agrees with
            // the gate.
            Assert.IsTrue(PickupAbsorbMath.TryComputeAbsorbVolumeLimit(0.50f, 10f, 512f,
                out limit));
            Assert.AreEqual(0.006545f, limit, 1e-6f);
            Assert.IsFalse(PickupAbsorbMath.IsBeyondAbsorbVolumeLimit(bounds, limit));
            AbsorbOutcome bigBall = PickupAbsorbMath.EvaluateAbsorbWithBallSize(true,
                "stone", Allowed, Allowed.Length, bounds, Vector3.zero, Vector3.zero,
                0.50f * 0.5f, 0f, 512f, MaxGrowthRadiusM, 0.50f, 10f);
            Assert.AreEqual(AbsorbOutcome.Absorb, bigBall);

            Assert.AreEqual(smallBall == AbsorbOutcome.RejectItemTooLarge,
                PickupAbsorbMath.IsBeyondAbsorbVolumeLimit(bounds, 0.001414f));
            Assert.AreEqual(bigBall == AbsorbOutcome.RejectItemTooLarge,
                PickupAbsorbMath.IsBeyondAbsorbVolumeLimit(bounds, 0.006545f));
        }

        [Test]
        public void ShrinkingRecomputesWithNoResidualState()
        {
            // Both boxes are built from binary-exact fractions so the volumes are exact:
            // 1.25 * 0.5 * 0.25 = 0.15625 m^3 and 0.5 * 0.25 * 0.125 = 0.015625 m^3.
            // (A 0.1 m side would not do: 0.1f is inexact and a 1.25 * 0.1 * 0.1 product
            // lands one ulp ABOVE 0.0125, which would make an "equality" case a lie.)
            Vector3 bench = new Vector3(1.25f, 0.5f, 0.25f);
            Vector3 pebble = new Vector3(0.5f, 0.25f, 0.125f);

            // Same inputs, different limits: the answer only depends on the arguments,
            // because nothing is cached. A 0.15625 m^3 limit includes the bench at
            // equality, a 0.15 m^3 limit does not, and the pebble passes both.
            Assert.IsFalse(PickupAbsorbMath.IsBeyondAbsorbVolumeLimit(bench, 0.15625f));
            Assert.IsFalse(PickupAbsorbMath.IsBeyondAbsorbVolumeLimit(pebble, 0.15625f));
            Assert.IsTrue(PickupAbsorbMath.IsBeyondAbsorbVolumeLimit(bench, 0.15f));
            Assert.IsFalse(PickupAbsorbMath.IsBeyondAbsorbVolumeLimit(pebble, 0.15f));
            Assert.IsFalse(PickupAbsorbMath.IsBeyondAbsorbVolumeLimit(bench, 0.15625f));

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
                PickupAbsorbMath.IsBeyondAbsorbVolumeLimit(bench, 0.15f),
                PickupAbsorbMath.IsBeyondAbsorbVolumeLimit(pebble, 0.15f)
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
