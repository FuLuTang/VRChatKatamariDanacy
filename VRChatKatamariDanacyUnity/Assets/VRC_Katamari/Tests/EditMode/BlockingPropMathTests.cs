using NUnit.Framework;
using UnityEngine;
using VRCKatamari.Pickup;

namespace VRCKatamari.Tests
{
    /// <summary>
    /// EditMode port of Docs/tests/pickup_absorb/test_blocking_props.py, the same ten
    /// cases (1-6, 9, 10 plus the 7/8 limit-edge cases; see Docs/Design/PickupAbsorb/COLLIDER_PROXY_RUNTIME.md §4.1),
    /// followed by the four wiring-contract cases (11-12): a prop with no collider
    /// blocks from its gameplay bounds, growth past the limit removes the box in the same
    /// call, an absorbed prop is not a candidate, and only the nearest capacity blocks.
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

        private static float ReqMm(Vector3 bounds)
        {
            float reqMm;
            Assert.IsTrue(PickupAbsorbMath.TryComputePickupReqDiameterMm(bounds,
                PickupAbsorbMath.DefaultVolumeK, out reqMm));
            return reqMm;
        }

        private static AbsorbOutcome Gate(float ballDiameterM, Vector3 bounds)
        {
            return PickupAbsorbMath.EvaluateAbsorb(true, "stone", Allowed, Allowed.Length,
                bounds, Quaternion.identity, Vector3.zero, Vector3.zero, ballDiameterM * 0.5f, 0f,
                PickupAbsorbMath.DefaultVolumeK, ballDiameterM, MaxGrowthRadiusM);
        }

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
        public void ReqEqualToTheBallDiameterIsAbsorbable()
        {
            // Equality is inclusive on the ORIGINAL quantity: Req_mm == ball_mm is
            // absorbable, one millimetre less blocks. 1 x 0.5 x 1 m at k=0.70 is
            // V = 0.35 m^3, so Req = floor(1000*cbrt(60*0.35/pi)) = 1883 mm; a 1.883 m
            // ball absorbs and a 1.882 m ball does not.
            Vector3 block = new Vector3(1f, 0.5f, 1f);
            Assert.AreEqual(1883f, ReqMm(block));
            Assert.IsFalse(PickupAbsorbMath.IsBeyondAbsorbLimit(block,
                PickupAbsorbMath.DefaultVolumeK, 1.883f));
            Assert.IsTrue(PickupAbsorbMath.IsBeyondAbsorbLimit(block,
                PickupAbsorbMath.DefaultVolumeK, 1.882f));

            // The compared quantity is the item's VOLUME, not one axis and not the longest
            // edge: at the same 1.883 m ball a 0.85 m bat (Req 165 mm) and a 0.9 x 0.9 x
            // 0.6 m block (0.486 m^3 -> Req 1866 mm) are food, while a 0.9 m cube
            // (0.729 m^3 -> Req 2136 mm) blocks - its longest edge is the same 0.9 m as
            // the block's.
            Assert.IsFalse(PickupAbsorbMath.IsBeyondAbsorbLimit(
                new Vector3(0.02f, 0.02f, 0.85f), PickupAbsorbMath.DefaultVolumeK, 1.883f));
            Assert.IsFalse(PickupAbsorbMath.IsBeyondAbsorbLimit(
                new Vector3(0.9f, 0.9f, 0.6f), PickupAbsorbMath.DefaultVolumeK, 1.883f));
            Assert.IsTrue(PickupAbsorbMath.IsBeyondAbsorbLimit(
                new Vector3(0.9f, 0.9f, 0.9f), PickupAbsorbMath.DefaultVolumeK, 1.883f));

            // Bad data counts as beyond: a NaN comparison is never true, and letting it
            // through would leave the prop both unabsorbable and non-blocking.
            Assert.IsTrue(PickupAbsorbMath.IsBeyondAbsorbLimit(
                new Vector3(float.NaN, 0f, 0f), PickupAbsorbMath.DefaultVolumeK, 1f));
            Assert.IsTrue(PickupAbsorbMath.IsBeyondAbsorbLimit(
                new Vector3(-1f, 0.05f, 0.05f), PickupAbsorbMath.DefaultVolumeK, 1f));
            Assert.IsTrue(PickupAbsorbMath.IsBeyondAbsorbLimit(
                new Vector3(0.5f, 0.05f, 0.05f), PickupAbsorbMath.DefaultVolumeK,
                float.PositiveInfinity));
            Assert.IsTrue(PickupAbsorbMath.IsBeyondAbsorbLimit(
                new Vector3(0.5f, 0.05f, 0.05f), 0f, 1f));
            // A volume that overflows float is bad data too, not "infinitely big".
            Assert.IsTrue(PickupAbsorbMath.IsBeyondAbsorbLimit(
                new Vector3(1e20f, 1e20f, 1e20f), PickupAbsorbMath.DefaultVolumeK, 1f));
            // A zero-volume row can never exceed any requirement, so it never blocks: this
            // is why the host requires strictly positive gameplay bounds.
            Assert.IsFalse(PickupAbsorbMath.IsBeyondAbsorbLimit(
                new Vector3(0.5f, 0f, 0.5f), PickupAbsorbMath.DefaultVolumeK, 0.001f));
        }

        [Test]
        public void GrowthCrossingTheRequirementFlipsTheSameFunction()
        {
            // A 1.25 x 0.05 x 0.05 m plank: V = 0.70 * 0.003125 = 0.0021875 m^3, so
            // Req = floor(1000*cbrt(60*0.0021875/pi)) = 346 mm.
            Vector3 bounds = new Vector3(1.25f, 0.05f, 0.05f);
            Assert.AreEqual(346f, ReqMm(bounds));

            // A 0.30 m ball (300 mm) is short: the plank blocks and the gate refuses it.
            AbsorbOutcome smallBall = Gate(0.30f, bounds);
            Assert.AreEqual(AbsorbOutcome.RejectItemTooLarge, smallBall);
            Assert.IsTrue(PickupAbsorbMath.IsBeyondAbsorbLimit(bounds,
                PickupAbsorbMath.DefaultVolumeK, 0.30f));

            // Growing to 0.35 m (350 mm) crosses the 346 mm requirement: the same plank
            // becomes absorbable, and the host-side predicate agrees with the gate.
            AbsorbOutcome bigBall = Gate(0.35f, bounds);
            Assert.AreEqual(AbsorbOutcome.Absorb, bigBall);
            Assert.IsFalse(PickupAbsorbMath.IsBeyondAbsorbLimit(bounds,
                PickupAbsorbMath.DefaultVolumeK, 0.35f));

            Assert.AreEqual(smallBall == AbsorbOutcome.RejectItemTooLarge,
                PickupAbsorbMath.IsBeyondAbsorbLimit(bounds, PickupAbsorbMath.DefaultVolumeK,
                    0.30f));
            Assert.AreEqual(bigBall == AbsorbOutcome.RejectItemTooLarge,
                PickupAbsorbMath.IsBeyondAbsorbLimit(bounds, PickupAbsorbMath.DefaultVolumeK,
                    0.35f));
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

            // Same inputs, different balls: the answer only depends on the arguments,
            // because nothing is cached. The bench's Req is 1278 mm and the pebble's 593 mm
            // (V = 0.70 * 0.15625 = 0.109375 m^3 and 0.70 * 0.015625 = 0.0109375 m^3), so a
            // 1278 mm ball includes the bench at equality, a 1277 mm ball does not, and
            // the pebble passes both.
            Assert.AreEqual(1278f, ReqMm(bench));
            Assert.AreEqual(593f, ReqMm(pebble));
            Assert.IsFalse(PickupAbsorbMath.IsBeyondAbsorbLimit(bench,
                PickupAbsorbMath.DefaultVolumeK, 1.278f));
            Assert.IsFalse(PickupAbsorbMath.IsBeyondAbsorbLimit(pebble,
                PickupAbsorbMath.DefaultVolumeK, 1.278f));
            Assert.IsTrue(PickupAbsorbMath.IsBeyondAbsorbLimit(bench,
                PickupAbsorbMath.DefaultVolumeK, 1.277f));
            Assert.IsFalse(PickupAbsorbMath.IsBeyondAbsorbLimit(pebble,
                PickupAbsorbMath.DefaultVolumeK, 1.277f));
            Assert.IsFalse(PickupAbsorbMath.IsBeyondAbsorbLimit(bench,
                PickupAbsorbMath.DefaultVolumeK, 1.278f));

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
                PickupAbsorbMath.IsBeyondAbsorbLimit(bench,
                    PickupAbsorbMath.DefaultVolumeK, 1.277f),
                PickupAbsorbMath.IsBeyondAbsorbLimit(pebble,
                    PickupAbsorbMath.DefaultVolumeK, 1.277f)
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

        // ------------------------------------------------------------------
        // The wiring's contract: gameplay bounds (no collider), the volume gate as the
        // only predicate, absorbed props excluded, nearest-capacity selection.
        // ------------------------------------------------------------------

        [Test]
        public void APropWithoutAColliderBlocksFromItsGameplayBounds()
        {
            // This build path has no collider parameter at all: the box is the gameplay
            // volume around the prop's world position (Docs/02_TECHNICAL_CONTRACTS.md's
            // three-layer split; the picnic lawn authors props with no collider at all).
            Vector3 propPosition = new Vector3(0f, 0f, 2f);
            Vector3 propBounds = new Vector3(0.9f, 0.9f, 0.9f);

            Vector3 minimum;
            Vector3 maximum;
            Assert.IsTrue(BlockingPropMath.TryBoxFromCentreAndExtents(propPosition, propBounds,
                out minimum, out maximum));
            Assert.AreEqual(-0.45f, minimum.x, Tolerance);
            Assert.AreEqual(-0.45f, minimum.y, Tolerance);
            Assert.AreEqual(1.55f, minimum.z, Tolerance);
            Assert.AreEqual(0.45f, maximum.x, Tolerance);
            Assert.AreEqual(0.45f, maximum.y, Tolerance);
            Assert.AreEqual(2.45f, maximum.z, Tolerance);

            float distance;
            Assert.IsTrue(BlockingPropMath.TryDistanceToBox(Vector3.zero, minimum, maximum,
                out distance));
            Assert.AreEqual(1.55f, distance, Tolerance);

            int[] indices = new int[1];
            float[] distances = new float[1];
            Assert.AreEqual(1, Select(Vector3.zero, 2f, new Vector3[] { minimum },
                new Vector3[] { maximum }, new bool[] { true }, 1, null, 0, 0f,
                indices, distances, 1));
            Assert.AreEqual(0, indices[0]);
            Assert.AreEqual(1.55f, distances[0], Tolerance);
        }

        [Test]
        public void GrowingPastTheRequirementRemovesTheBoxInTheSameCall()
        {
            // A 1.25 x 0.05 x 0.05 m plank is 0.003125 m^3 of bbox, 0.0021875 m^3 at
            // k=0.70, Req 346 mm. The candidate flag is the absorb gate's own predicate
            // (IsBeyondAbsorbLimit) with the prop's k and the ball's diameter, so this is
            // the exact pair the host calls - no second threshold anywhere in the test.
            Vector3 plank = new Vector3(1.25f, 0.05f, 0.05f);
            Vector3 minimum;
            Vector3 maximum;
            Assert.IsTrue(BlockingPropMath.TryBoxFromCentreAndExtents(
                new Vector3(0f, 0f, 1f), plank, out minimum, out maximum));
            Vector3[] minima = new Vector3[] { minimum };
            Vector3[] maxima = new Vector3[] { maximum };
            int[] indices = new int[1];
            float[] distances = new float[1];

            bool blocksForASmallBall = PickupAbsorbMath.IsBeyondAbsorbLimit(plank,
                PickupAbsorbMath.DefaultVolumeK, 0.30f);
            Assert.IsTrue(blocksForASmallBall);
            Assert.AreEqual(1, Select(Vector3.zero, 2f, minima, maxima,
                new bool[] { blocksForASmallBall }, 1, null, 0, 0f, indices, distances, 1));
            Assert.AreEqual(0, indices[0]);

            bool blocksForAGrownBall = PickupAbsorbMath.IsBeyondAbsorbLimit(plank,
                PickupAbsorbMath.DefaultVolumeK, 0.35f);
            Assert.IsFalse(blocksForAGrownBall);
            // The same call with the same arrays now reports no box: "the box disappears"
            // is the selection's answer, not a separate removal path.
            Assert.AreEqual(0, Select(Vector3.zero, 2f, minima, maxima,
                new bool[] { blocksForAGrownBall }, 1, null, 0, 0f, indices, distances, 1));

            // And the gate agrees on both sides of the same requirement, so the two
            // systems cannot disagree about "too big" at any diameter.
            Assert.AreEqual(AbsorbOutcome.RejectItemTooLarge, Gate(0.30f, plank));
            Assert.AreEqual(AbsorbOutcome.Absorb, Gate(0.35f, plank));
        }

        [Test]
        public void AnAbsorbedPropIsNotABlockingCandidate()
        {
            // Two props, both beyond the limit and both near the ball. The host marks a
            // prop's candidate flag false once it is attached or hidden, so one flag is
            // the whole removal path - there is no "unregister this prop" call.
            Vector3 leftMin = new Vector3(-2f, -0.5f, -0.5f);
            Vector3 leftMax = new Vector3(-1f, 0.5f, 0.5f);
            Vector3 rightMin = new Vector3(1f, -0.5f, -0.5f);
            Vector3 rightMax = new Vector3(2f, 0.5f, 0.5f);
            Vector3[] minima = new Vector3[] { leftMin, rightMin };
            Vector3[] maxima = new Vector3[] { leftMax, rightMax };
            int[] indices = new int[2];
            float[] distances = new float[2];

            // Prop 0 was absorbed (candidate false); only prop 1 may block.
            Assert.AreEqual(1, Select(Vector3.zero, 3f, minima, maxima,
                new bool[] { false, true }, 2, null, 0, 0f, indices, distances, 2));
            Assert.AreEqual(1, indices[0]);
            Assert.AreEqual(1f, distances[0], 0f);

            // Both absorbed: the motor is told "no blocking boxes", which is what makes an
            // eaten prop stop being a wall on the next scan.
            Assert.AreEqual(0, Select(Vector3.zero, 3f, minima, maxima,
                new bool[] { false, false }, 2, null, 0, 0f, indices, distances, 2));
        }

        [Test]
        public void OnlyTheNearestCapacityPropsBlock()
        {
            // Eight props in a line at z = 1..8 m, each a 0.2 m cube, so the near faces are
            // at 0.9, 1.9, ... 7.9 m. The motor's blocking capacity is six: the two
            // farthest must not become walls even though they are inside this radius.
            Vector3[] minima = new Vector3[8];
            Vector3[] maxima = new Vector3[8];
            bool[] candidate = new bool[8];
            for (int i = 0; i < 8; i++)
            {
                float centreZ = (float)(i + 1);
                minima[i] = new Vector3(-0.1f, -0.1f, centreZ - 0.1f);
                maxima[i] = new Vector3(0.1f, 0.1f, centreZ + 0.1f);
                candidate[i] = true;
            }

            int[] indices = new int[6];
            float[] distances = new float[6];
            Assert.AreEqual(6, Select(Vector3.zero, 10f, minima, maxima, candidate, 8,
                null, 0, 0f, indices, distances, 6));
            for (int i = 0; i < 6; i++)
            {
                Assert.AreEqual(i, indices[i]);
                Assert.AreEqual(0.9f + (float)i, distances[i], Tolerance);
            }
            // The two farthest (near faces at 6.9 and 7.9 m) are the ones left out.
            for (int i = 0; i < 6; i++)
            {
                Assert.AreNotEqual(6, indices[i]);
                Assert.AreNotEqual(7, indices[i]);
            }

            // Shrinking the walking margin to the ball's own radius drops the far props:
            // at 1 m only the first near face (0.9 m) is inside.
            Assert.AreEqual(1, Select(Vector3.zero, 1f, minima, maxima, candidate, 8,
                null, 0, 0f, indices, distances, 6));
            Assert.AreEqual(0, indices[0]);
            Assert.AreEqual(0.9f, distances[0], Tolerance);
        }
    }
}
