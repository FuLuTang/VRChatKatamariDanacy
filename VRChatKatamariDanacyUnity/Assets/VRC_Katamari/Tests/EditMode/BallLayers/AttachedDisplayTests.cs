using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using VRCKatamari.BallLayers;

namespace VRCKatamari.Tests
{
    /// <summary>
    /// AttachedDisplayMath: the "biggest N + most recent N" display cap and the original's
    /// bury rule. Expected sets come from an independent LINQ statement of the definition
    /// (sort everything, take N, skip duplicates), never from the incremental code.
    /// </summary>
    public class AttachedDisplayTests
    {
        private sealed class Ball
        {
            public readonly long[] Key;
            public readonly int[] Seq;
            public readonly bool[] Buried;
            public readonly int[] Far;
            public readonly int[] Hist;
            public readonly int[] Big;
            public readonly int[] Recent;
            public readonly int[] Sorted;
            public int HistCount;
            public int BigCount;
            public int RecentCount;
            public int SortedCount;
            public int Cursor;
            public readonly int BigCap;
            public readonly int RecentCap;
            public int Rebuilds;

            public Ball(int rows, int bigCap, int recentCap)
            {
                Key = new long[rows];
                Seq = new int[rows];
                Buried = new bool[rows];
                Far = new int[rows];
                Hist = new int[rows];
                Big = new int[Math.Max(bigCap, 1)];
                Recent = new int[Math.Max(recentCap, 1)];
                Sorted = new int[rows];
                BigCap = bigCap;
                RecentCap = recentCap;
                AttachedDisplayMath.ResetRows(Seq, Buried, rows);
            }

            public void Attach(int row, bool buried, out int shown, out int hidden)
            {
                Assert.IsTrue(AttachedDisplayMath.TryAttach(row, buried, Key, Seq, Buried, Hist,
                    ref HistCount, Big, ref BigCount, BigCap, Recent, ref RecentCount, RecentCap,
                    out shown, out hidden));
                if (!buried)
                    Assert.IsTrue(AttachedDisplayMath.TryInsertByFar(row, Far, Sorted, ref SortedCount, Cursor));
            }

            // The host's per-frame bury pass: mark everything first, rebuild at most once.
            public int BuryUpTo(int buryRadiusMm)
            {
                bool needRebuild = false;
                int buried = 0;
                int row;
                while ((row = AttachedDisplayMath.NextToBury(Far, Sorted, SortedCount, ref Cursor, buryRadiusMm)) >= 0)
                {
                    if (AttachedDisplayMath.MarkBuried(row, Seq, Buried, Big, BigCount, Recent, RecentCount))
                        needRebuild = true;
                    buried++;
                }
                if (needRebuild)
                {
                    Rebuilds++;
                    Assert.IsTrue(AttachedDisplayMath.TryRebuild(Key, Seq, Buried, Hist, HistCount,
                        Big, ref BigCount, BigCap, Recent, ref RecentCount, RecentCap));
                }
                return buried;
            }

            public HashSet<int> Shown()
            {
                var set = new HashSet<int>();
                for (int i = 0; i < BigCount; i++) set.Add(Big[i]);
                for (int i = 0; i < RecentCount; i++) set.Add(Recent[i]);
                return set;
            }

            // The definition, stated independently.
            public HashSet<int> Expected()
            {
                var live = Enumerable.Range(0, HistCount).Select(i => Hist[i]).Where(r => !Buried[r]).ToList();
                var big = live.OrderByDescending(r => Key[r]).ThenByDescending(r => Seq[r]).Take(BigCap).ToList();
                var recent = live.Where(r => !big.Contains(r)).OrderByDescending(r => Seq[r]).Take(RecentCap);
                return new HashSet<int>(big.Concat(recent));
            }
        }

        [Test]
        public void OwnerExampleBiggestIsSixthMostRecentSoRecentReachesTheEleventh()
        {
            // 20 equal props, except the 15th attached (6th most recent of 20) is huge.
            var ball = new Ball(20, 5, 5);
            for (int i = 0; i < 20; i++)
            {
                ball.Key[i] = i == 14 ? 9000L : 10L;
                ball.Attach(i, false, out _, out _);
            }
            // Biggest 5: row 14, then the four newest equal-key rows 19, 18, 17, 16 (later first).
            // Recent 5 among the rest: 15, 13, 12, 11, 10.
            CollectionAssert.AreEquivalent(new[] { 14, 19, 18, 17, 16, 15, 13, 12, 11, 10 }, ball.Shown().ToArray());
            CollectionAssert.AreEquivalent(ball.Expected().ToArray(), ball.Shown().ToArray());
        }

        [Test]
        public void OwnerExampleWithDistinctSizesSkipsTheDuplicate()
        {
            // Recent group only differs from the plain "last five" by skipping the big one.
            var ball = new Ball(12, 1, 5);
            for (int i = 0; i < 12; i++)
            {
                ball.Key[i] = i == 6 ? 1000L : 1L;   // row 6 is the 6th most recent of 12
                ball.Attach(i, false, out _, out _);
            }
            CollectionAssert.AreEquivalent(new[] { 6, 11, 10, 9, 8, 7 }, ball.Shown().ToArray());

            var ball2 = new Ball(12, 1, 6);
            for (int i = 0; i < 12; i++)
            {
                ball2.Key[i] = i == 6 ? 1000L : 1L;
                ball2.Attach(i, false, out _, out _);
            }
            // Most recent six excluding row 6: 11..7 and 5 (the 7th most recent).
            CollectionAssert.AreEquivalent(new[] { 6, 11, 10, 9, 8, 7, 5 }, ball2.Shown().ToArray());
        }

        [Test]
        public void IncrementalAttachMatchesTheDefinitionAndChangesAtMostTwoRows()
        {
            var random = new System.Random(20261123);
            for (int trial = 0; trial < 200; trial++)
            {
                int bigCap = trial % 3 == 0 ? 5 : 10;
                int recentCap = trial % 5 == 0 ? 0 : (trial % 2 == 0 ? 5 : 10);
                int rows = random.Next(1, 160);
                var ball = new Ball(rows, bigCap, recentCap);
                var before = new HashSet<int>();
                for (int i = 0; i < rows; i++)
                {
                    // Many ties on purpose.
                    ball.Key[i] = random.Next(0, 2) == 0 ? 500L : random.Next(1, 1000);
                    int shown, hidden;
                    ball.Attach(i, false, out shown, out hidden);
                    var after = ball.Shown();
                    CollectionAssert.AreEquivalent(ball.Expected().ToArray(), after.ToArray(), "trial " + trial + " row " + i);
                    var diff = new HashSet<int>(after);
                    diff.SymmetricExceptWith(before);
                    Assert.LessOrEqual(diff.Count, 2);
                    var reported = new HashSet<int>();
                    if (shown >= 0) reported.Add(shown);
                    if (hidden >= 0) reported.Add(hidden);
                    CollectionAssert.AreEquivalent(diff.ToArray(), reported.ToArray());
                    before = after;
                }
                Assert.LessOrEqual(before.Count, bigCap + recentCap);
            }
        }

        [Test]
        public void RebuildEqualsIncrementalState()
        {
            var random = new System.Random(7);
            var ball = new Ball(300, 10, 10);
            for (int i = 0; i < 300; i++)
            {
                ball.Key[i] = random.Next(1, 50);
                ball.Attach(i, false, out _, out _);
            }
            var incremental = ball.Shown();
            Assert.IsTrue(AttachedDisplayMath.TryRebuild(ball.Key, ball.Seq, ball.Buried, ball.Hist,
                ball.HistCount, ball.Big, ref ball.BigCount, 10, ball.Recent, ref ball.RecentCount, 10));
            CollectionAssert.AreEquivalent(incremental.ToArray(), ball.Shown().ToArray());
        }

        [Test]
        public void PropBuriedAtAttachIsNeverShown()
        {
            var ball = new Ball(3, 10, 10);
            ball.Key[0] = 1000000L;
            int shown, hidden;
            ball.Attach(0, true, out shown, out hidden);
            Assert.AreEqual(-1, shown);
            Assert.AreEqual(-1, hidden);
            Assert.IsEmpty(ball.Shown());
            ball.Attach(1, false, out shown, out _);
            Assert.AreEqual(1, shown);
        }

        [Test]
        public void BuryRadiusFollowsTheWlkCurve()
        {
            // R = 1 m, Rs = 0.5 m: base = 0.5 + 0.38 * 0.5 = 0.69 m.
            int mm;
            Assert.IsTrue(AttachedDisplayMath.TryBuryRadiusMm(1f, 0.5f, 0, out mm));
            Assert.AreEqual(690, mm, 1);
            Assert.IsTrue(AttachedDisplayMath.TryBuryRadiusMm(1f, 0.5f, 150, out mm));
            Assert.AreEqual(690, mm, 1);
            // n = 225: halfway from base to R.
            Assert.IsTrue(AttachedDisplayMath.TryBuryRadiusMm(1f, 0.5f, 225, out mm));
            Assert.AreEqual(845, mm, 1);
            Assert.IsTrue(AttachedDisplayMath.TryBuryRadiusMm(1f, 0.5f, 300, out mm));
            Assert.AreEqual(1000, mm, 1);
            Assert.IsTrue(AttachedDisplayMath.TryBuryRadiusMm(1f, 0.5f, 5000, out mm));
            Assert.AreEqual(1000, mm, 1);
            // At the start size the bury sphere is the start ball: nothing attached at the
            // surface can be inside it.
            Assert.IsTrue(AttachedDisplayMath.TryBuryRadiusMm(0.111f, 0.111f, 0, out mm));
            Assert.AreEqual(111, mm);
            // Shrunk below the start (cannot happen in play, but must not invert the curve).
            Assert.IsTrue(AttachedDisplayMath.TryBuryRadiusMm(0.1f, 0.2f, 0, out mm));
            Assert.AreEqual(100, mm);
            Assert.IsFalse(AttachedDisplayMath.TryBuryRadiusMm(float.NaN, 0.1f, 0, out mm));
            Assert.IsFalse(AttachedDisplayMath.TryBuryRadiusMm(1f, 0f, 0, out mm));
            Assert.IsFalse(AttachedDisplayMath.TryBuryRadiusMm(1f, 0.5f, -1, out mm));
        }

        [Test]
        public void BuryCursorMatchesBruteForceAndNeverRewindsOnShrink()
        {
            var random = new System.Random(99);
            var ball = new Ball(400, 10, 10);
            int radius = 100;
            for (int i = 0; i < 400; i++)
            {
                ball.Key[i] = random.Next(1, 100);
                ball.Far[i] = radius + random.Next(0, 400);
                bool buriedNow = ball.Far[i] < radius;
                ball.Attach(i, buriedNow, out _, out _);
                if (i % 7 == 0) radius += random.Next(0, 30);
                if (i == 250) radius -= 200; // a bank shrink
                ball.BuryUpTo(radius);
                for (int r = 0; r <= i; r++)
                {
                    // Every live row is at or outside the current bury radius.
                    if (ball.Buried[r]) continue;
                    Assert.GreaterOrEqual(ball.Far[r], radius);
                }
                CollectionAssert.AreEquivalent(ball.Expected().ToArray(), ball.Shown().ToArray());
            }
            // After the shrink nothing came back.
            int buriedCount = ball.Buried.Count(b => b);
            ball.BuryUpTo(0);
            Assert.AreEqual(buriedCount, ball.Buried.Count(b => b));
        }

        [Test]
        public void MassBuryInOneFrameRebuildsOnce()
        {
            var random = new System.Random(3);
            var ball = new Ball(800, 10, 10);
            for (int i = 0; i < 800; i++)
            {
                ball.Key[i] = random.Next(1, 1000);
                ball.Far[i] = 1000 + i;          // older props deeper inside, like a growing ball
                ball.Attach(i, false, out _, out _);
            }
            Assert.AreEqual(0, ball.BuryUpTo(1000));
            int rebuildsBefore = ball.Rebuilds;
            // One growth step buries 600 props at once, including shown big ones.
            Assert.AreEqual(600, ball.BuryUpTo(1600));
            Assert.AreEqual(rebuildsBefore + 1, ball.Rebuilds);
            CollectionAssert.AreEquivalent(ball.Expected().ToArray(), ball.Shown().ToArray());
            Assert.IsTrue(ball.Shown().All(r => !ball.Buried[r]));
            // The next frame with an unchanged radius does nothing.
            Assert.AreEqual(0, ball.BuryUpTo(1600));
            Assert.AreEqual(rebuildsBefore + 1, ball.Rebuilds);
        }

        [Test]
        public void HiddenRowsAreNotRebuiltWhenNoShownRowIsBuried()
        {
            var ball = new Ball(50, 2, 2);
            for (int i = 0; i < 50; i++)
            {
                ball.Key[i] = 1L;
                ball.Far[i] = 100 + i;
                ball.Attach(i, false, out _, out _);
            }
            // The four shown rows are the newest (equal keys): burying old rows touches nothing.
            Assert.AreEqual(10, ball.BuryUpTo(110));
            Assert.AreEqual(0, ball.Rebuilds);
            CollectionAssert.AreEquivalent(ball.Expected().ToArray(), ball.Shown().ToArray());
        }

        [Test]
        public void MillimetreKeysAreDeterministic()
        {
            int a, b;
            Assert.IsTrue(AttachedDisplayMath.TryFloorMm(0.12345f, out a));
            Assert.IsTrue(AttachedDisplayMath.TryFloorMm(0.12345f, out b));
            Assert.AreEqual(a, b);
            Assert.AreEqual(123, a);
            Assert.IsFalse(AttachedDisplayMath.TryFloorMm(-0.001f, out a));
            Assert.IsFalse(AttachedDisplayMath.TryFloorMm(float.PositiveInfinity, out a));
            Assert.AreEqual(100L * 200L * 300L, AttachedDisplayMath.VolumeKey(new Vector3(0.1f, 0.2f, 0.3f)));
            Assert.AreEqual(0L, AttachedDisplayMath.VolumeKey(new Vector3(0f, 1f, 1f)));
        }

        [Test]
        public void RefusesBadInputWithoutChangingState()
        {
            var ball = new Ball(4, 2, 2);
            ball.Attach(0, false, out _, out _);
            int shown, hidden;
            // Attaching the same row twice is refused.
            Assert.IsFalse(AttachedDisplayMath.TryAttach(0, false, ball.Key, ball.Seq, ball.Buried,
                ball.Hist, ref ball.HistCount, ball.Big, ref ball.BigCount, 2, ball.Recent,
                ref ball.RecentCount, 2, out shown, out hidden));
            Assert.IsFalse(AttachedDisplayMath.TryAttach(9, false, ball.Key, ball.Seq, ball.Buried,
                ball.Hist, ref ball.HistCount, ball.Big, ref ball.BigCount, 2, ball.Recent,
                ref ball.RecentCount, 2, out shown, out hidden));
            Assert.AreEqual(1, ball.HistCount);
            Assert.IsFalse(AttachedDisplayMath.MarkBuried(3, ball.Seq, ball.Buried, ball.Big,
                ball.BigCount, ball.Recent, ball.RecentCount));
            Assert.IsFalse(ball.Buried[3]);
        }
    }
}
