using System;
using NUnit.Framework;
using UnityEngine;
using VRCKatamari.Pickup;

namespace VRCKatamari.Tests
{
    /// <summary>
    /// Pins the pickup host's spatial grid: whatever the field looks like, a query returns
    /// EVERY member whose centre is within the radius (it may return more, never fewer), and
    /// the grid is a true partition of the members. The oracle is the brute-force walk over all
    /// rows, which is what the host did before the grid existed.
    /// </summary>
    public class PickupSpatialGridTests
    {
        private sealed class Grid
        {
            public float[] Info;
            public int[] CellStart;
            public int[] CellRows;
            public Vector3[] CellMin;
            public Vector3[] CellMax;
            public Vector3[] Positions;
            public bool[] Member;
            public int Members;
        }

        private static Grid Build(Vector3[] positions, bool[] member, float cellSize, int maxCells)
        {
            var grid = new Grid();
            grid.Positions = positions;
            grid.Member = member;
            grid.Info = new float[PickupSpatialGrid.InfoLength];
            Assert.IsTrue(PickupSpatialGrid.TryComputeLayout(positions, member, positions.Length,
                cellSize, maxCells, grid.Info));
            int cells = PickupSpatialGrid.CellCount(grid.Info);
            grid.Members = (int)grid.Info[PickupSpatialGrid.InfoMemberCount];
            grid.CellStart = new int[cells + 1];
            grid.CellRows = new int[grid.Members];
            grid.CellMin = new Vector3[cells];
            grid.CellMax = new Vector3[cells];
            Assert.IsTrue(PickupSpatialGrid.BuildCells(grid.Info, positions, member,
                positions.Length, grid.CellStart, grid.CellRows));
            Assert.IsTrue(PickupSpatialGrid.BuildCellBounds(grid.Info, positions, grid.CellStart,
                grid.CellRows, grid.CellMin, grid.CellMax));
            return grid;
        }

        private static int Query(Grid grid, Vector3 point, float radius, int[] output)
        {
            return PickupSpatialGrid.Query(point, radius, grid.Info, grid.CellStart, grid.CellRows,
                grid.CellMin, grid.CellMax, output);
        }

        private static Vector3[] RandomPositions(System.Random random, int count, float spreadXz,
            float spreadY)
        {
            var positions = new Vector3[count];
            for (int i = 0; i < count; i++)
            {
                positions[i] = new Vector3(
                    (float)(random.NextDouble() * 2.0 - 1.0) * spreadXz,
                    (float)(random.NextDouble() * 2.0 - 1.0) * spreadY,
                    (float)(random.NextDouble() * 2.0 - 1.0) * spreadXz);
            }
            return positions;
        }

        private static bool[] AllMembers(int count)
        {
            var member = new bool[count];
            for (int i = 0; i < count; i++) member[i] = true;
            return member;
        }

        // Every member inside the radius is returned; nothing is returned twice or from outside
        // the member set.
        private static void AssertCovers(Grid grid, Vector3 point, float radius, string label)
        {
            var output = new int[grid.Members];
            int found = Query(grid, point, radius, output);
            Assert.GreaterOrEqual(found, 0, label);
            var returned = new bool[grid.Positions.Length];
            for (int k = 0; k < found; k++)
            {
                int row = output[k];
                Assert.IsTrue(grid.Member[row], label + ": row " + row + " is not a member");
                Assert.IsFalse(returned[row], label + ": row " + row + " returned twice");
                returned[row] = true;
            }

            float radiusSq = radius * radius;
            for (int i = 0; i < grid.Positions.Length; i++)
            {
                if (!grid.Member[i]) continue;
                float dx = grid.Positions[i].x - point.x;
                float dy = grid.Positions[i].y - point.y;
                float dz = grid.Positions[i].z - point.z;
                if (dx * dx + dy * dy + dz * dz <= radiusSq)
                {
                    Assert.IsTrue(returned[i], label + ": row " + i + " is within " + radius
                        + " m of the point but was not returned");
                }
            }
        }

        [Test]
        public void ARandomFieldIsCoveredByEveryQuery()
        {
            for (int seed = 1; seed <= 6; seed++)
            {
                var random = new System.Random(seed);
                Vector3[] positions = RandomPositions(random, 600, 40f, 6f);
                Grid grid = Build(positions, AllMembers(positions.Length), 2f, 4096);
                for (int q = 0; q < 200; q++)
                {
                    Vector3 point = RandomPositions(random, 1, 50f, 8f)[0];
                    float radius = (float)(random.NextDouble() * 6.0);
                    AssertCovers(grid, point, radius, "seed " + seed + " query " + q);
                }
            }
        }

        [Test]
        public void QueriesOnRowPositionsAndOnTheExactBoundaryAreCovered()
        {
            var random = new System.Random(77);
            Vector3[] positions = RandomPositions(random, 300, 20f, 3f);
            Grid grid = Build(positions, AllMembers(positions.Length), 1.5f, 4096);
            for (int i = 0; i < positions.Length; i += 3)
            {
                // A query centred on a row with the radius reaching exactly another row.
                int other = (i * 7 + 11) % positions.Length;
                float radius = Vector3.Distance(positions[i], positions[other]);
                AssertCovers(grid, positions[i], radius, "boundary " + i);
                AssertCovers(grid, positions[i], 0f, "zero radius " + i);
            }
        }

        [Test]
        public void ARadiusLargerThanTheFieldReturnsEveryMember()
        {
            var random = new System.Random(5);
            Vector3[] positions = RandomPositions(random, 120, 10f, 2f);
            Grid grid = Build(positions, AllMembers(positions.Length), 2f, 4096);
            var output = new int[grid.Members];
            Assert.AreEqual(120, Query(grid, Vector3.zero, 1000f, output));
        }

        [Test]
        public void QueriesFarOutsideTheFieldAreCoveredAndReturnNothing()
        {
            var random = new System.Random(9);
            Vector3[] positions = RandomPositions(random, 100, 10f, 2f);
            Grid grid = Build(positions, AllMembers(positions.Length), 2f, 4096);
            var output = new int[grid.Members];
            Assert.AreEqual(0, Query(grid, new Vector3(500f, 0f, 500f), 3f, output));
            Assert.AreEqual(0, Query(grid, new Vector3(-500f, 100f, 0f), 3f, output));
            // Just outside the field edge the clamped cell range must still find edge rows.
            AssertCovers(grid, new Vector3(11.5f, 0f, 0f), 3f, "edge x");
            AssertCovers(grid, new Vector3(0f, 0f, -11.5f), 3f, "edge z");
        }

        [Test]
        public void OnlyMembersAreIndexed()
        {
            var random = new System.Random(21);
            Vector3[] positions = RandomPositions(random, 200, 15f, 2f);
            var member = new bool[200];
            for (int i = 0; i < 200; i++) member[i] = i % 3 != 0;
            Grid grid = Build(positions, member, 2f, 4096);
            for (int q = 0; q < 50; q++)
            {
                Vector3 point = RandomPositions(random, 1, 15f, 2f)[0];
                AssertCovers(grid, point, 4f, "member subset " + q);
            }
        }

        [Test]
        public void ALimitOnTheCellCountEnlargesTheCellButKeepsTheCoverage()
        {
            var random = new System.Random(33);
            Vector3[] positions = RandomPositions(random, 400, 200f, 5f);
            Grid grid = Build(positions, AllMembers(positions.Length), 0.5f, 64);
            Assert.LessOrEqual(PickupSpatialGrid.CellCount(grid.Info), 64);
            Assert.Greater(grid.Info[PickupSpatialGrid.InfoCellSize], 0.5f,
                "the cell had to grow to stay under the limit");
            for (int q = 0; q < 100; q++)
            {
                Vector3 point = RandomPositions(random, 1, 220f, 6f)[0];
                AssertCovers(grid, point, (float)(random.NextDouble() * 10.0), "coarse " + q);
            }
        }

        [Test]
        public void ADegenerateFieldWorks()
        {
            // One member.
            Grid one = Build(new[] { new Vector3(3f, 1f, -2f) }, new[] { true }, 2f, 16);
            AssertCovers(one, new Vector3(3f, 1f, -2f), 0f, "single at point");
            AssertCovers(one, new Vector3(5f, 1f, -2f), 1.9f, "single near");
            var output = new int[1];
            Assert.AreEqual(0, Query(one, new Vector3(50f, 0f, 0f), 1f, output));

            // Many members on one point (zero span): one cell, all returned together.
            var same = new Vector3[10];
            for (int i = 0; i < same.Length; i++) same[i] = new Vector3(1f, 2f, 3f);
            Grid pile = Build(same, AllMembers(10), 2f, 16);
            var pileOut = new int[10];
            Assert.AreEqual(10, Query(pile, new Vector3(1f, 2f, 3f), 0f, pileOut));
        }

        [Test]
        public void TheGridIsAPartitionOfTheMembersInAscendingRowOrder()
        {
            var random = new System.Random(4);
            Vector3[] positions = RandomPositions(random, 250, 12f, 2f);
            var member = AllMembers(positions.Length);
            member[10] = false;
            member[11] = false;
            Grid grid = Build(positions, member, 2f, 4096);
            int cells = PickupSpatialGrid.CellCount(grid.Info);
            Assert.AreEqual(0, grid.CellStart[0]);
            Assert.AreEqual(grid.Members, grid.CellStart[cells]);
            var seen = new bool[positions.Length];
            for (int c = 0; c < cells; c++)
            {
                Assert.LessOrEqual(grid.CellStart[c], grid.CellStart[c + 1]);
                for (int k = grid.CellStart[c]; k < grid.CellStart[c + 1]; k++)
                {
                    int row = grid.CellRows[k];
                    Assert.IsFalse(seen[row], "row " + row + " is in two cells");
                    seen[row] = true;
                    Assert.AreEqual(c, PickupSpatialGrid.CellOf(grid.Info, positions[row].x,
                        positions[row].z), "row " + row + " is in the wrong cell");
                    if (k > grid.CellStart[c]) Assert.Greater(row, grid.CellRows[k - 1]);
                    Assert.GreaterOrEqual(positions[row].x, grid.CellMin[c].x);
                    Assert.LessOrEqual(positions[row].x, grid.CellMax[c].x);
                    Assert.GreaterOrEqual(positions[row].y, grid.CellMin[c].y);
                    Assert.LessOrEqual(positions[row].y, grid.CellMax[c].y);
                    Assert.GreaterOrEqual(positions[row].z, grid.CellMin[c].z);
                    Assert.LessOrEqual(positions[row].z, grid.CellMax[c].z);
                }
            }
            for (int i = 0; i < positions.Length; i++) Assert.AreEqual(member[i], seen[i]);
        }

        [Test]
        public void ASmallQueryTouchesFarFewerRowsThanTheField()
        {
            // The point of the grid: a ball-sized query over a spread-out field looks at a small
            // fraction of the rows (generous bound, so it pins the order of magnitude, not a
            // tuning).
            var random = new System.Random(8);
            Vector3[] positions = RandomPositions(random, 500, 30f, 1f);
            Grid grid = Build(positions, AllMembers(positions.Length), 2f, 4096);
            var output = new int[grid.Members];
            int total = 0;
            for (int q = 0; q < 100; q++)
            {
                Vector3 point = RandomPositions(random, 1, 30f, 1f)[0];
                total += Query(grid, point, 2.5f, output);
            }
            Assert.Less(total / 100, 500 / 4, "a 2.5 m query should not return a quarter of the field");
        }

        [Test]
        public void InvalidInputsAreRefusedWithoutWriting()
        {
            Vector3[] positions = { Vector3.zero, Vector3.one };
            bool[] member = { true, true };
            var info = new float[PickupSpatialGrid.InfoLength];
            Assert.IsFalse(PickupSpatialGrid.TryComputeLayout(null, member, 2, 2f, 16, info));
            Assert.IsFalse(PickupSpatialGrid.TryComputeLayout(positions, null, 2, 2f, 16, info));
            Assert.IsFalse(PickupSpatialGrid.TryComputeLayout(positions, member, 3, 2f, 16, info));
            Assert.IsFalse(PickupSpatialGrid.TryComputeLayout(positions, member, 2, 0f, 16, info));
            Assert.IsFalse(PickupSpatialGrid.TryComputeLayout(positions, member, 2, float.NaN, 16, info));
            Assert.IsFalse(PickupSpatialGrid.TryComputeLayout(positions, member, 2, 2f, 0, info));
            Assert.IsFalse(PickupSpatialGrid.TryComputeLayout(positions, member, 2, 2f, 16,
                new float[2]));
            Assert.IsFalse(PickupSpatialGrid.TryComputeLayout(positions, new[] { false, false }, 2,
                2f, 16, info), "no members");
            Assert.IsFalse(PickupSpatialGrid.TryComputeLayout(
                new[] { Vector3.zero, new Vector3(float.NaN, 0f, 0f) }, member, 2, 2f, 16, info),
                "a non-finite member position is the caller's error");

            Grid grid = Build(positions, member, 2f, 16);
            var small = new int[1];
            Assert.AreEqual(-1, Query(grid, Vector3.zero, 1f, small), "output smaller than members");
            var output = new int[2];
            Assert.AreEqual(-1, Query(grid, new Vector3(float.NaN, 0f, 0f), 1f, output));
            Assert.AreEqual(-1, Query(grid, Vector3.zero, -1f, output));
            Assert.AreEqual(-1, Query(grid, Vector3.zero, float.PositiveInfinity, output));
        }
    }
}
