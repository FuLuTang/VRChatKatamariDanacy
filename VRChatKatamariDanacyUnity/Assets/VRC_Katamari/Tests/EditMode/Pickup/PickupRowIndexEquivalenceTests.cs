using System;
using NUnit.Framework;
using UnityEngine;
using VRCKatamari.Pickup;

namespace VRCKatamari.Tests
{
    /// <summary>
    /// Pins that the pickup host's row index changes WHICH rows are looked at, never what is
    /// decided about them: the cached size test equals the gate's predicate, the nearest-few
    /// selection and the nearest-solid selection do not depend on the order rows are visited
    /// in, and a grid-plus-linear-list walk reaches the same candidate selection and the same
    /// set of rows through the obstacle prune as the full walk over every row.
    /// </summary>
    /// <remarks>
    /// The host itself needs Unity (GameObjects), so the last group re-states its gather loop
    /// over plain arrays (same expressions, same order of tests) and compares it with the
    /// pre-index full walk built from the unchanged public pieces (OfferProp,
    /// IsBeyondAbsorbLimit). The Unity-only host tests drive the real component.
    /// </remarks>
    public class PickupRowIndexEquivalenceTests
    {
        // ---- the cached size test is the gate's predicate -----------------------------------

        [Test]
        public void TheCachedRequirementGivesTheGatesVerdictForEveryInput()
        {
            float[] sizes = { 0f, 0.001f, 0.02f, 0.11f, 0.5f, 1.3f, 4f, 40f, 400f };
            float[] ks = { 0.05f, 0.7f, 1f, 8f, 32f, 0f, -1f, 33f, float.NaN, float.PositiveInfinity };
            float[] diameters = { 0f, 0.0004f, 0.222f, 1f, 1.186f, 2.2f, 30f, -0.5f, float.NaN,
                float.PositiveInfinity, float.MaxValue };
            int checkedCombos = 0;
            foreach (float a in sizes)
            {
                foreach (float b in sizes)
                {
                    var size = new Vector3(a, b, (a + b) * 0.5f);
                    foreach (float k in ks)
                    {
                        float req = PickupAbsorbMath.ResolveRequirementMmOrInvalid(size, k);
                        foreach (float d in diameters)
                        {
                            float ballMm;
                            bool ballValid = PickupAbsorbMath.TryFloorMillimetres(d, out ballMm);
                            Assert.AreEqual(PickupAbsorbMath.IsBeyondAbsorbLimit(size, k, d),
                                PickupAbsorbMath.IsBeyondCachedRequirement(req, ballValid, ballMm),
                                "size " + size + " k " + k + " diameter " + d);
                            checkedCombos++;
                        }
                    }
                }
            }
            Assert.Greater(checkedCombos, 7000, "the sweep must be the denominator it claims");
        }

        [Test]
        public void TheCachedRequirementAtTheOneMillimetreBoundary()
        {
            var size = new Vector3(0.5f, 0.5f, 0.5f);
            float req = PickupAbsorbMath.ResolveRequirementMmOrInvalid(size,
                PickupAbsorbMath.DefaultVolumeK);
            Assert.AreEqual(1186f, req);
            Assert.IsTrue(PickupAbsorbMath.IsBeyondCachedRequirement(req, true, 1185f));
            Assert.IsFalse(PickupAbsorbMath.IsBeyondCachedRequirement(req, true, 1186f));
            Assert.AreEqual(PickupAbsorbMath.RequirementInvalid,
                PickupAbsorbMath.ResolveRequirementMmOrInvalid(new Vector3(-1f, 1f, 1f),
                    PickupAbsorbMath.DefaultVolumeK));
        }

        // ---- the nearest-few selection does not depend on the visiting order ------------------

        private static void Shuffle(int[] values, System.Random random)
        {
            for (int i = values.Length - 1; i > 0; i--)
            {
                int j = random.Next(i + 1);
                int t = values[i];
                values[i] = values[j];
                values[j] = t;
            }
        }

        [Test]
        public void TheNearestFewDoNotDependOnTheOrderRowsAreOffered()
        {
            var random = new System.Random(12);
            for (int trial = 0; trial < 300; trial++)
            {
                int rows = 5 + random.Next(40);
                var distSq = new float[rows];
                for (int i = 0; i < rows; i++)
                {
                    // Few distinct values, so ties are common.
                    distSq[i] = random.Next(6) * 0.25f;
                }
                int limit = 1 + random.Next(6);

                int[] ascendingIdx = new int[8];
                float[] ascendingD = new float[8];
                int ascending = 0;
                for (int i = 0; i < rows; i++)
                {
                    ascending = PickupCandidateSelection.InsertNearest(i, distSq[i], ascendingIdx,
                        ascendingD, ascending, limit);
                }

                var order = new int[rows];
                for (int i = 0; i < rows; i++) order[i] = i;
                Shuffle(order, random);
                int[] shuffledIdx = new int[8];
                float[] shuffledD = new float[8];
                int shuffled = 0;
                for (int k = 0; k < rows; k++)
                {
                    shuffled = PickupCandidateSelection.InsertNearest(order[k], distSq[order[k]],
                        shuffledIdx, shuffledD, shuffled, limit);
                }

                Assert.AreEqual(ascending, shuffled, "trial " + trial);
                for (int s = 0; s < ascending; s++)
                {
                    Assert.AreEqual(ascendingIdx[s], shuffledIdx[s], "trial " + trial + " slot " + s);
                    Assert.AreEqual(ascendingD[s], shuffledD[s], "trial " + trial + " slot " + s);
                }

                // And the ascending result is what a strict-less walk keeps: equal distances
                // rank the lower row first.
                for (int s = 1; s < ascending; s++)
                {
                    Assert.IsTrue(ascendingD[s - 1] < ascendingD[s]
                        || (ascendingD[s - 1] == ascendingD[s] && ascendingIdx[s - 1] < ascendingIdx[s]));
                }
            }
        }

        [Test]
        public void OfferMeasuredAndOfferPropAgree()
        {
            var random = new System.Random(3);
            var sizes = new Vector3[40];
            var ks = new float[40];
            var positions = new Vector3[40];
            for (int i = 0; i < 40; i++)
            {
                float s = 0.02f + (float)random.NextDouble() * 0.8f;
                sizes[i] = new Vector3(s, s * 0.7f, s * 1.3f);
                ks[i] = PickupAbsorbMath.DefaultVolumeK;
                positions[i] = new Vector3((float)random.NextDouble() * 4f - 2f, 0f,
                    (float)random.NextDouble() * 4f - 2f);
            }

            for (int mm = 100; mm <= 1500; mm += 50)
            {
                float diameter = mm / 1000f;
                int[] idxA = new int[4];
                float[] dA = new float[4];
                int[] idxB = new int[4];
                float[] dB = new float[4];
                int selA = 0;
                int selB = 0;
                float ballMm;
                bool ballValid = PickupAbsorbMath.TryFloorMillimetres(diameter, out ballMm);
                for (int i = 0; i < 40; i++)
                {
                    int afterA;
                    int offerA = PickupCandidateSelection.OfferProp(i, positions[i], Vector3.zero,
                        1.6f, sizes, ks, diameter, idxA, dA, selA, 4, out afterA);
                    selA = afterA;

                    float distSq = positions[i].x * positions[i].x + positions[i].y * positions[i].y
                        + positions[i].z * positions[i].z;
                    int afterB;
                    int offerB = PickupCandidateSelection.OfferMeasured(i, distSq, 1.6f,
                        PickupAbsorbMath.IsBeyondCachedRequirement(
                            PickupAbsorbMath.ResolveRequirementMmOrInvalid(sizes[i], ks[i]),
                            ballValid, ballMm),
                        idxB, dB, selB, 4, out afterB);
                    selB = afterB;
                    Assert.AreEqual(offerA, offerB, "row " + i + " diameter " + mm);
                }
                Assert.AreEqual(selA, selB);
                for (int s = 0; s < selA; s++)
                {
                    Assert.AreEqual(idxA[s], idxB[s]);
                    Assert.AreEqual(dA[s], dB[s]);
                }
            }
        }

        // ---- nearest solids: the list version equals the pre-index full-table version ----------

        // The pre-index algorithm (full walk over rows 0..count-1, ContainsIndex), kept here as the
        // oracle the list version must match bit for bit.
        private static int ReferenceSelectNearest(Vector3 ball, float radius, Vector3[] minima,
            Vector3[] maxima, bool[] candidate, int count, int[] previous, int previousCount,
            float stickiness, int[] outIndices, float[] outDistances, int capacity)
        {
            float stickyRadius = radius + stickiness;
            int written = 0;
            for (int slot = 0; slot < previousCount; slot++)
            {
                if (written >= capacity) break;
                int index = previous[slot];
                if (index < 0 || index >= count) continue;
                if (!candidate[index]) continue;
                if (Contains(outIndices, written, index)) continue;
                float distance = Distance(ball, minima[index], maxima[index]);
                if (distance > stickyRadius) continue;
                outIndices[written] = index;
                outDistances[written] = distance;
                written++;
            }
            while (written < capacity)
            {
                int best = -1;
                float bestDistance = 0f;
                for (int i = 0; i < count; i++)
                {
                    if (!candidate[i]) continue;
                    if (Contains(outIndices, written, i)) continue;
                    float distance = Distance(ball, minima[i], maxima[i]);
                    if (distance > radius) continue;
                    if (best < 0 || distance < bestDistance)
                    {
                        best = i;
                        bestDistance = distance;
                    }
                }
                if (best < 0) break;
                outIndices[written] = best;
                outDistances[written] = bestDistance;
                written++;
            }
            return written;
        }

        private static bool Contains(int[] values, int count, int value)
        {
            for (int i = 0; i < count; i++)
            {
                if (values[i] == value) return true;
            }
            return false;
        }

        private static float Distance(Vector3 p, Vector3 lo, Vector3 hi)
        {
            float dx = p.x - Mathf.Clamp(p.x, lo.x, hi.x);
            float dy = p.y - Mathf.Clamp(p.y, lo.y, hi.y);
            float dz = p.z - Mathf.Clamp(p.z, lo.z, hi.z);
            return Mathf.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        [Test]
        public void TheCandidateListSelectionEqualsTheFullTableSelection()
        {
            var random = new System.Random(2024);
            int comparisons = 0;
            for (int trial = 0; trial < 400; trial++)
            {
                int count = 5 + random.Next(60);
                var minima = new Vector3[count];
                var maxima = new Vector3[count];
                var candidate = new bool[count];
                for (int i = 0; i < count; i++)
                {
                    // Boxes on a coarse lattice so equal distances (ties) occur.
                    float x = random.Next(-6, 7) * 0.5f;
                    float z = random.Next(-6, 7) * 0.5f;
                    minima[i] = new Vector3(x, 0f, z);
                    maxima[i] = new Vector3(x + 0.5f, 0.5f, z + 0.5f);
                    candidate[i] = random.Next(3) != 0;
                }
                var previous = new int[12];
                int previousCount = random.Next(10);
                for (int s = 0; s < previousCount; s++) previous[s] = random.Next(-1, count + 2);

                int capacity = 1 + random.Next(12);
                float radius = (float)random.NextDouble() * 3f;
                float stickiness = random.Next(2) == 0 ? 0f : 0.25f;
                Vector3 ball = new Vector3(random.Next(-4, 5) * 0.25f, 0f, random.Next(-4, 5) * 0.25f);

                var refIdx = new int[12];
                var refDist = new float[12];
                int expected = ReferenceSelectNearest(ball, radius, minima, maxima, candidate, count,
                    previous, previousCount, stickiness, refIdx, refDist, capacity);

                // The wrapper (full table in, list built inside).
                var wrapIdx = new int[12];
                var wrapDist = new float[12];
                int wrapped = BlockingPropMath.SelectNearest(ball, radius, minima, maxima, candidate,
                    count, previous, previousCount, stickiness, wrapIdx, wrapDist, capacity);
                Assert.AreEqual(expected, wrapped, "trial " + trial);

                // The list version with the candidate rows in a shuffled order.
                int listCount = 0;
                var rows = new int[count];
                for (int i = 0; i < count; i++)
                {
                    if (candidate[i]) rows[listCount++] = i;
                }
                var shuffled = new int[listCount];
                Array.Copy(rows, shuffled, listCount);
                Shuffle(shuffled, random);
                var listIdx = new int[12];
                var listDist = new float[12];
                var scratch = new float[count];
                int listed = BlockingPropMath.SelectNearestAmong(ball, radius, minima, maxima,
                    candidate, shuffled, listCount, count, scratch, previous, previousCount,
                    stickiness, listIdx, listDist, capacity);
                Assert.AreEqual(expected, listed, "trial " + trial);
                for (int s = 0; s < expected; s++)
                {
                    Assert.AreEqual(refIdx[s], wrapIdx[s], "wrapper trial " + trial + " slot " + s);
                    Assert.AreEqual(refDist[s], wrapDist[s]);
                    Assert.AreEqual(refIdx[s], listIdx[s], "list trial " + trial + " slot " + s);
                    Assert.AreEqual(refDist[s], listDist[s]);
                    comparisons++;
                }
            }
            Assert.Greater(comparisons, 1000, "the property run must exercise real selections");
        }

        [Test]
        public void TheListSelectionRefusesWhatTheFullTableRefusesAndWritesNothing()
        {
            var minima = new[] { Vector3.zero, new Vector3(2f, 2f, 2f) };
            var maxima = new[] { Vector3.one, Vector3.one }; // row 1 is inverted
            var candidate = new[] { true, true };
            var rows = new[] { 0, 1 };
            var outIdx = new[] { 77, 77 };
            var outDist = new[] { 7f, 7f };
            var scratch = new float[2];
            Assert.AreEqual(-1, BlockingPropMath.SelectNearestAmong(Vector3.zero, 3f, minima, maxima,
                candidate, rows, 2, 2, scratch, new int[2], 0, 0.25f, outIdx, outDist, 2));
            Assert.AreEqual(77, outIdx[0]);
            Assert.AreEqual(7f, outDist[0]);
            // A list entry outside the table is refused too.
            Assert.AreEqual(-1, BlockingPropMath.SelectNearestAmong(Vector3.zero, 3f, minima, maxima,
                candidate, new[] { 0, 5 }, 2, 2, scratch, new int[2], 0, 0.25f, outIdx, outDist, 2));
            Assert.AreEqual(-1, BlockingPropMath.SelectNearest(Vector3.zero, 3f, minima, maxima,
                candidate, 2, new int[2], 0, 0.25f, outIdx, outDist, 2));
        }

        // ---- grid + linear list reaches the same decisions as the full walk -----------------------

        private sealed class Field
        {
            public Vector3[] StartPositions;
            public Vector3[] LivePositions;
            public Vector3[] OwnSizes;
            public float[] HalfDiagonal;
            public float[] Req;
            public float[] K;
            public bool[] Mobile;
            public bool[] Free;
        }

        private static Field RandomField(System.Random random, int count)
        {
            var f = new Field();
            f.StartPositions = new Vector3[count];
            f.LivePositions = new Vector3[count];
            f.OwnSizes = new Vector3[count];
            f.HalfDiagonal = new float[count];
            f.Req = new float[count];
            f.K = new float[count];
            f.Mobile = new bool[count];
            f.Free = new bool[count];
            for (int i = 0; i < count; i++)
            {
                f.StartPositions[i] = new Vector3((float)(random.NextDouble() * 2 - 1) * 25f,
                    (float)random.NextDouble() * 4f, (float)(random.NextDouble() * 2 - 1) * 25f);
                // Most props are small, a few are big (half diagonal up to ~9 m).
                float scale = random.Next(12) == 0 ? 8f + (float)random.NextDouble() * 10f
                    : 0.05f + (float)random.NextDouble() * 1.2f;
                f.OwnSizes[i] = new Vector3(scale, scale * (0.3f + (float)random.NextDouble()),
                    scale * (0.3f + (float)random.NextDouble()));
                f.HalfDiagonal[i] = Mathf.Sqrt((f.OwnSizes[i].x * f.OwnSizes[i].x
                    + f.OwnSizes[i].y * f.OwnSizes[i].y + f.OwnSizes[i].z * f.OwnSizes[i].z)) * 0.5f;
                f.K[i] = PickupAbsorbMath.DefaultVolumeK;
                f.Req[i] = PickupAbsorbMath.ResolveRequirementMmOrInvalid(f.OwnSizes[i], f.K[i]);
                f.Mobile[i] = random.Next(10) < 4;
                f.Free[i] = random.Next(8) != 0;
                f.LivePositions[i] = f.StartPositions[i];
                if (f.Mobile[i])
                {
                    f.LivePositions[i] += new Vector3((float)(random.NextDouble() * 2 - 1) * 8f, 0f,
                        (float)(random.NextDouble() * 2 - 1) * 8f);
                }
            }
            return f;
        }

        [Test]
        public void TheIndexedWalkSelectsAndPrunesExactlyLikeTheFullWalk()
        {
            const float Activation = 1.6f;
            const float Margin = 0.8f;
            const float Stickiness = 0.25f;
            const float BigHalfDiagonal = 1.5f;
            var random = new System.Random(555);
            int selectionsCompared = 0;
            int prunedRows = 0;

            for (int fieldSeed = 0; fieldSeed < 8; fieldSeed++)
            {
                int count = 300 + random.Next(300);
                Field f = RandomField(random, count);

                // Build exactly like the host: static rows with a small box go in the grid, the
                // rest (mobile, static big) in the linear list.
                var member = new bool[count];
                var linear = new System.Collections.Generic.List<int>();
                float gridMaxHalfDiagonal = 0f;
                int members = 0;
                for (int i = 0; i < count; i++)
                {
                    if (f.Mobile[i] || f.HalfDiagonal[i] > BigHalfDiagonal)
                    {
                        linear.Add(i);
                        continue;
                    }
                    member[i] = true;
                    members++;
                    if (f.HalfDiagonal[i] > gridMaxHalfDiagonal) gridMaxHalfDiagonal = f.HalfDiagonal[i];
                }
                Assume.That(members > 0);
                var info = new float[PickupSpatialGrid.InfoLength];
                Assert.IsTrue(PickupSpatialGrid.TryComputeLayout(f.StartPositions, member, count,
                    2f, 4096, info));
                int cells = PickupSpatialGrid.CellCount(info);
                var cellStart = new int[cells + 1];
                var cellRows = new int[members];
                var cellMin = new Vector3[cells];
                var cellMax = new Vector3[cells];
                Assert.IsTrue(PickupSpatialGrid.BuildCells(info, f.StartPositions, member, count,
                    cellStart, cellRows));
                Assert.IsTrue(PickupSpatialGrid.BuildCellBounds(info, f.StartPositions, cellStart,
                    cellRows, cellMin, cellMax));
                var buffer = new int[count];

                for (int q = 0; q < 60; q++)
                {
                    Vector3 center = new Vector3((float)(random.NextDouble() * 2 - 1) * 28f,
                        (float)random.NextDouble() * 4f, (float)(random.NextDouble() * 2 - 1) * 28f);
                    float diameter = 0.1f + (float)random.NextDouble() * 2.1f;
                    float collisionRadius = diameter * 0.5f;
                    float pruneRadius = collisionRadius + Margin + Stickiness;

                    // ---- the full walk (pre-index behaviour) ----
                    var fullIdx = new int[4];
                    var fullDist = new float[4];
                    int fullSelected = 0;
                    int fullInRange = 0;
                    int fullWithin = 0;
                    var fullPruned = new System.Collections.Generic.List<int>();
                    for (int i = 0; i < count; i++)
                    {
                        if (!f.Free[i]) continue;
                        int after;
                        int offer = PickupCandidateSelection.OfferProp(i, f.LivePositions[i], center,
                            Activation, f.OwnSizes, f.K, diameter, fullIdx, fullDist, fullSelected,
                            4, out after);
                        fullSelected = after;
                        if (offer != PickupCandidateSelection.OfferOutOfRange)
                        {
                            fullInRange++;
                            if (offer == PickupCandidateSelection.OfferWithinLimit) fullWithin++;
                        }

                        Vector3 p = f.LivePositions[i];
                        float dx = p.x - center.x;
                        float dy = p.y - center.y;
                        float dz = p.z - center.z;
                        float distSq = dx * dx + dy * dy + dz * dz;
                        float pruneLimit = pruneRadius + f.HalfDiagonal[i];
                        if (distSq > pruneLimit * pruneLimit) continue;
                        if (!PickupAbsorbMath.IsBeyondAbsorbLimit(f.OwnSizes[i], f.K[i], diameter))
                            continue;
                        fullPruned.Add(i);
                    }

                    // ---- the indexed walk (what the host does) ----
                    float gridReach = Mathf.Max(Activation, pruneRadius + gridMaxHalfDiagonal);
                    int total = PickupSpatialGrid.Query(center, gridReach, info, cellStart, cellRows,
                        cellMin, cellMax, buffer);
                    Assert.GreaterOrEqual(total, 0);
                    for (int k = 0; k < linear.Count; k++) buffer[total++] = linear[k];

                    float ballMm;
                    bool ballValid = PickupAbsorbMath.TryFloorMillimetres(diameter, out ballMm);
                    float scanSq = Activation * Activation;
                    var nearRows = new System.Collections.Generic.List<int>();
                    var nearDist = new System.Collections.Generic.List<float>();
                    for (int k = 0; k < total; k++)
                    {
                        int row = buffer[k];
                        if (!f.Free[row]) continue;
                        Vector3 p = f.Mobile[row] ? f.LivePositions[row] : f.StartPositions[row];
                        float dx = p.x - center.x;
                        float dy = p.y - center.y;
                        float dz = p.z - center.z;
                        float distSq = dx * dx + dy * dy + dz * dz;
                        float limit = pruneRadius + f.HalfDiagonal[row];
                        float limitSq = Mathf.Max(scanSq, limit * limit);
                        if (distSq > limitSq) continue;
                        nearRows.Add(row);
                        nearDist.Add(distSq);
                    }

                    var idxIdx = new int[4];
                    var idxDist = new float[4];
                    int idxSelected = 0;
                    int idxInRange = 0;
                    int idxWithin = 0;
                    var idxPruned = new System.Collections.Generic.List<int>();
                    for (int k = 0; k < nearRows.Count; k++)
                    {
                        int i = nearRows[k];
                        int after;
                        int offer = PickupCandidateSelection.OfferMeasured(i, nearDist[k], Activation,
                            PickupAbsorbMath.IsBeyondCachedRequirement(f.Req[i], ballValid, ballMm),
                            idxIdx, idxDist, idxSelected, 4, out after);
                        idxSelected = after;
                        if (offer != PickupCandidateSelection.OfferOutOfRange)
                        {
                            idxInRange++;
                            if (offer == PickupCandidateSelection.OfferWithinLimit) idxWithin++;
                        }

                        float pruneLimit = pruneRadius + f.HalfDiagonal[i];
                        if (nearDist[k] > pruneLimit * pruneLimit) continue;
                        if (!PickupAbsorbMath.IsBeyondCachedRequirement(f.Req[i], ballValid, ballMm))
                            continue;
                        idxPruned.Add(i);
                    }

                    string label = "field " + fieldSeed + " query " + q;
                    Assert.AreEqual(fullInRange, idxInRange, label + " inRange");
                    Assert.AreEqual(fullWithin, idxWithin, label + " withinLimit");
                    Assert.AreEqual(fullSelected, idxSelected, label + " selected");
                    for (int s = 0; s < fullSelected; s++)
                    {
                        Assert.AreEqual(fullIdx[s], idxIdx[s], label + " slot " + s);
                        Assert.AreEqual(fullDist[s], idxDist[s], label + " slot " + s);
                    }

                    // The rows through the obstacle prune are the same SET (the indexed walk
                    // visits them in another order; the nearest-solid selection is order-free).
                    idxPruned.Sort();
                    fullPruned.Sort();
                    CollectionAssert.AreEqual(fullPruned, idxPruned, label + " pruned rows");
                    selectionsCompared++;
                    prunedRows += fullPruned.Count;
                }
            }

            Assert.AreEqual(8 * 60, selectionsCompared);
            Assert.Greater(prunedRows, 500, "the property run must reach real obstacle candidates");
        }
    }
}
