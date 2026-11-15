using NUnit.Framework;
using UnityEngine;
using VRCKatamari.BallLayers;

namespace VRCKatamari.Tests
{
    /// <summary>
    /// EditMode port of Docs/tests/ball_radial_proxy/test_radial_proxy.py. Expected
    /// values are hand calculations or geometric invariants (a support is a
    /// distance along a unit direction; the hemisphere filter is a dot-product
    /// sign; the Fibonacci set is unit length), never a re-run of the
    /// implementation. Lengths are metres in world space with +Y up, matching the
    /// Python model.
    /// </summary>
    public class RadialProxyTests
    {
        private const float Tolerance = 1e-5f;

        /// <summary>
        /// Test-only driver that owns the arrays a behaviour will own: the 16
        /// directions, the 16 supports, a flattened ball-local endpoint pool, the
        /// per-item slices and the candidate slots. Every decision is a call into
        /// RadialProxyMath; the driver exists because the layer is deliberately
        /// stateless, so attach/detach composition needs a caller.
        /// </summary>
        private sealed class ProxyShell
        {
            public const int PoolCapacity = 16;
            public const int ItemCapacity = 8;

            public readonly Vector3[] Directions = new Vector3[RadialProxyMath.DirectionCount];
            public readonly float[] Supports = new float[RadialProxyMath.DirectionCount];
            public readonly string[] CandidateKeys = new string[RadialProxyMath.MaxExtremeCandidates];
            public readonly Vector3[] Pool = new Vector3[PoolCapacity];
            public readonly int[] ItemStarts = new int[ItemCapacity];
            public readonly int[] ItemLengths = new int[ItemCapacity];
            public readonly string[] ItemKeys = new string[ItemCapacity];
            public readonly bool[] ItemIsCandidate = new bool[ItemCapacity];
            public readonly int[] AffectedIndices = new int[RadialProxyMath.DirectionCount];

            public int CandidateCount;
            public int PoolCount;
            public int ItemCount;
            public int AffectedCount;
            public float CoreRadius;

            public ProxyShell(float coreRadius)
            {
                CoreRadius = coreRadius;
                Assert.IsTrue(RadialProxyMath.TryFillDirections(Directions));
                // No endpoints: every support sits exactly at the core.
                Assert.IsTrue(RadialProxyMath.TrySolveSupports(CoreRadius, Pool, PoolCount, Directions,
                    AllDirectionIndices(), RadialProxyMath.DirectionCount, Supports));
            }

            public int Attach(Vector3[] worldEndpoints, Vector3 ballPosition, Quaternion ballRotation,
                string key, bool isCandidate)
            {
                Vector3[] local = new Vector3[worldEndpoints.Length];
                Assert.IsTrue(RadialProxyMath.TryTransformEndpointsToLocal(worldEndpoints, worldEndpoints.Length,
                    ballPosition, ballRotation, local));
                Assert.LessOrEqual(PoolCount + local.Length, PoolCapacity);
                int start = PoolCount;
                for (int i = 0; i < local.Length; i++)
                {
                    Pool[PoolCount] = local[i];
                    PoolCount++;
                }
                Assert.IsTrue(RadialProxyMath.TryCollectAffectedDirections(local, local.Length, Directions,
                    AffectedIndices, out AffectedCount));
                Assert.IsTrue(RadialProxyMath.TrySolveSupports(CoreRadius, Pool, PoolCount, Directions,
                    AffectedIndices, AffectedCount, Supports));
                Assert.Less(ItemCount, ItemCapacity);
                ItemStarts[ItemCount] = start;
                ItemLengths[ItemCount] = local.Length;
                ItemKeys[ItemCount] = key;
                ItemIsCandidate[ItemCount] = isCandidate;
                ItemCount++;
                if (isCandidate)
                {
                    Assert.IsTrue(RadialProxyMath.TryInsertCandidateKey(CandidateKeys, ref CandidateCount, key));
                }
                return AffectedCount;
            }

            public void Detach(string key)
            {
                int item = IndexOfItem(key);
                Assert.GreaterOrEqual(item, 0, "unknown item " + key);
                int start = ItemStarts[item];
                int length = ItemLengths[item];
                // The affected set is a function of the item's own endpoints, so it is
                // recomputed from the same points instead of being stored per item.
                Vector3[] removed = new Vector3[length];
                for (int i = 0; i < length; i++) removed[i] = Pool[start + i];
                for (int i = start; i + length < PoolCount; i++) Pool[i] = Pool[i + length];
                PoolCount -= length;
                // The slices after the removed one moved down by its length.
                for (int i = 0; i < ItemCount; i++)
                {
                    if (ItemStarts[i] > start) ItemStarts[i] -= length;
                }
                for (int i = item; i < ItemCount - 1; i++)
                {
                    ItemStarts[i] = ItemStarts[i + 1];
                    ItemLengths[i] = ItemLengths[i + 1];
                    ItemKeys[i] = ItemKeys[i + 1];
                    ItemIsCandidate[i] = ItemIsCandidate[i + 1];
                }
                ItemCount--;
                Assert.IsTrue(RadialProxyMath.TryCollectAffectedDirections(removed, removed.Length, Directions,
                    AffectedIndices, out AffectedCount));
                Assert.IsTrue(RadialProxyMath.TrySolveSupports(CoreRadius, Pool, PoolCount, Directions,
                    AffectedIndices, AffectedCount, Supports));
                bool existed;
                Assert.IsTrue(RadialProxyMath.TryRemoveCandidateKey(CandidateKeys, ref CandidateCount, key,
                    out existed));
            }

            public bool Rebuild(float coreRadius)
            {
                int candidateCount;
                if (!RadialProxyMath.TryRebuild(coreRadius, Pool, ItemStarts, ItemLengths, ItemKeys,
                    ItemIsCandidate, ItemCount, Directions, Supports, CandidateKeys, out candidateCount))
                {
                    return false;
                }
                CoreRadius = coreRadius;
                CandidateCount = candidateCount;
                return true;
            }

            public int IndexOfItem(string key)
            {
                for (int i = 0; i < ItemCount; i++)
                {
                    if (ItemKeys[i] == key) return i;
                }
                return -1;
            }
        }

        /// <summary>A rebuild ledger entry: ball-local endpoints plus the item's stable key.</summary>
        private sealed class ShellEntry
        {
            public readonly Vector3[] WorldEndpoints;
            public readonly string Key;
            public readonly bool IsCandidate;

            public ShellEntry(Vector3[] worldEndpoints, string key, bool isCandidate)
            {
                WorldEndpoints = worldEndpoints;
                Key = key;
                IsCandidate = isCandidate;
            }
        }

        [Test]
        public void AttachUpdatesNearDirectionButNotRemoteDirection()
        {
            // The endpoint is 3 m along direction 0, so its own direction grows from
            // the 1 m core to 3 m and its protrusion is 2 m.
            ProxyShell shell = new ProxyShell(1f);
            Vector3 endpoint;
            Assert.IsTrue(RadialProxyMath.TryDirectionLengthPoint(shell.Directions[0], 3f, out endpoint));
            int affected = shell.Attach(new[] { endpoint }, Vector3.zero, Quaternion.identity, "rod#gen1", false);

            Assert.GreaterOrEqual(affected, 1);
            Assert.IsTrue(Contains(shell.AffectedIndices, shell.AffectedCount, 0));
            Assert.Greater(shell.Supports[0], 1f);
            Assert.AreEqual(3f, shell.Supports[0], 1e-4f);

            // The opposite hemisphere must not be touched by this attach.
            int remote = FirstDirectionBelow(shell.Directions, shell.Directions[0], -0.5f);
            Assert.GreaterOrEqual(remote, 0, "the Fibonacci set must contain a direction opposite direction 0");
            Assert.IsFalse(Contains(shell.AffectedIndices, shell.AffectedCount, remote));
            Assert.AreEqual(1f, shell.Supports[remote], Tolerance);

            float protrusion;
            Assert.IsTrue(RadialProxyMath.TryProtrusion(shell.Supports[0], shell.CoreRadius, out protrusion));
            Assert.AreEqual(shell.Supports[0] - 1f, protrusion, Tolerance);
        }

        [Test]
        public void SameDirectionContributionsSurviveLargestRemoval()
        {
            // Two rods on direction 7: after the long one is removed the short one's
            // 2 m support must take over, not the historical 4 m maximum.
            ProxyShell shell = new ProxyShell(1f);
            Vector3 shortPoint;
            Vector3 longPoint;
            Assert.IsTrue(RadialProxyMath.TryDirectionLengthPoint(shell.Directions[7], 2f, out shortPoint));
            Assert.IsTrue(RadialProxyMath.TryDirectionLengthPoint(shell.Directions[7], 4f, out longPoint));
            shell.Attach(new[] { shortPoint }, Vector3.zero, Quaternion.identity, "short#1", false);
            shell.Attach(new[] { longPoint }, Vector3.zero, Quaternion.identity, "long#1", false);
            Assert.AreEqual(4f, shell.Supports[7], 1e-4f);

            shell.Detach("long#1");
            Assert.AreEqual(2f, shell.Supports[7], 1e-4f);
            shell.Detach("short#1");
            Assert.AreEqual(1f, shell.Supports[7], 1e-4f);
        }

        [Test]
        public void DetachRecomputesAndCandidateSlotIsReleased()
        {
            ProxyShell shell = new ProxyShell(0.75f);
            Vector3 endpoint;
            Assert.IsTrue(RadialProxyMath.TryDirectionLengthPoint(shell.Directions[3], 4f, out endpoint));
            shell.Attach(new[] { endpoint }, Vector3.zero, Quaternion.identity, "long-rod#1", true);
            Assert.Greater(shell.Supports[3], 0.75f);
            AssertKeys(shell.CandidateKeys, shell.CandidateCount, new[] { "long-rod#1" });

            shell.Detach("long-rod#1");
            Assert.IsTrue(Contains(shell.AffectedIndices, shell.AffectedCount, 3));
            Assert.AreEqual(0.75f, shell.Supports[3], 1e-4f);
            Assert.AreEqual(0, shell.CandidateCount);

            // The freed candidate slot is reusable by the next long object.
            shell.Attach(new[] { new Vector3(3f, 0f, 0f) }, Vector3.zero, Quaternion.identity,
                "replacement#1", true);
            Assert.AreEqual(1, shell.CandidateCount);
            Assert.AreEqual("replacement#1", shell.CandidateKeys[0]);
        }

        [Test]
        public void FullRebuildMatchesIncrementalLedger()
        {
            // Three items, one of them a declared extreme candidate. Their order must
            // not matter: support is a max over the whole ledger and candidate keys
            // are sorted, so incremental attach and a from-scratch rebuild agree.
            ShellEntry[] entries =
            {
                new ShellEntry(new[] { new Vector3(2f, 0f, 0f) }, "a#1", false),
                new ShellEntry(new[] { new Vector3(0f, 0f, -3f) }, "b#1", true),
                new ShellEntry(new[] { new Vector3(0f, 2.5f, 0f) }, "c#1", false)
            };

            ProxyShell incremental = new ProxyShell(1f);
            for (int i = 0; i < entries.Length; i++)
            {
                incremental.Attach(entries[i].WorldEndpoints, Vector3.zero, Quaternion.identity,
                    entries[i].Key, entries[i].IsCandidate);
            }

            ProxyShell rebuilt = new ProxyShell(0.25f);
            for (int i = entries.Length - 1; i >= 0; i--)
            {
                rebuilt.Attach(entries[i].WorldEndpoints, Vector3.zero, Quaternion.identity,
                    entries[i].Key, entries[i].IsCandidate);
            }
            Assert.IsTrue(rebuilt.Rebuild(1f));
            AssertSupportsEqual(incremental.Supports, rebuilt.Supports);
            AssertKeys(incremental.CandidateKeys, incremental.CandidateCount, new[] { "b#1" });
            AssertKeys(rebuilt.CandidateKeys, rebuilt.CandidateCount, new[] { "b#1" });
        }

        [Test]
        public void CandidateIdsAreSortedAndLimitsRejectWithoutEviction()
        {
            // The model fixes the direction count at 16 and the candidate list at 0..4.
            Assert.IsFalse(RadialProxyMath.TryFillDirections(new Vector3[15]));
            Assert.IsFalse(RadialProxyMath.TryFillDirections(new Vector3[17]));
            Assert.IsFalse(RadialProxyMath.TryFillDirections(null));

            int count = 0;
            Assert.IsFalse(RadialProxyMath.TryInsertCandidateKey(new string[5], ref count, "x#1"));
            Assert.AreEqual(0, count);

            // A solve also refuses a direction set that is not the fixed 16.
            Assert.IsFalse(RadialProxyMath.TrySolveSupports(1f, new Vector3[0], 0, new Vector3[15],
                new int[0], 0, new float[RadialProxyMath.DirectionCount]));

            Vector3[] directions = new Vector3[RadialProxyMath.DirectionCount];
            Assert.IsTrue(RadialProxyMath.TryFillDirections(directions));
            // Geometric invariant of the fixed set: every entry is a unit vector.
            for (int i = 0; i < directions.Length; i++)
            {
                Assert.AreEqual(1f, directions[i].magnitude, 1e-5f, "direction " + i);
            }

            string[] keys = new string[RadialProxyMath.MaxExtremeCandidates];
            Assert.IsTrue(RadialProxyMath.TryInsertCandidateKey(keys, ref count, "zeta#1"));
            Assert.IsTrue(RadialProxyMath.TryInsertCandidateKey(keys, ref count, "alpha#1"));
            AssertKeys(keys, count, new[] { "alpha#1", "zeta#1" });
            Assert.IsTrue(RadialProxyMath.TryInsertCandidateKey(keys, ref count, "fill0#1"));
            Assert.IsTrue(RadialProxyMath.TryInsertCandidateKey(keys, ref count, "fill1#1"));
            Assert.AreEqual(4, count);
            string[] before = Prefix(keys, count);

            // A fifth candidate is refused; no surviving candidate is evicted.
            Assert.IsFalse(RadialProxyMath.TryInsertCandidateKey(keys, ref count, "overflow#1"));
            Assert.AreEqual(4, count);
            CollectionAssert.AreEqual(before, Prefix(keys, count));
        }

        [Test]
        public void DirectionAndLengthFormEndpointInAllComponents()
        {
            Vector3 point;
            Assert.IsTrue(RadialProxyMath.TryDirectionLengthPoint(new Vector3(0f, 0f, 1f), 2.5f, out point));
            AssertVector(new Vector3(0f, 0f, 2.5f), point);

            // A non-unit direction, a negative length and infinity are all rejected.
            Assert.IsFalse(RadialProxyMath.TryDirectionLengthPoint(new Vector3(0f, 0f, 2f), 2.5f, out point));
            Assert.IsFalse(RadialProxyMath.TryDirectionLengthPoint(new Vector3(0f, 0f, 1f), -1f, out point));
            Assert.IsFalse(RadialProxyMath.TryDirectionLengthPoint(new Vector3(0f, 0f, 1f),
                float.PositiveInfinity, out point));
            AssertVector(Vector3.zero, point);
        }

        [Test]
        public void QuaternionDirectionProbeAndWorldPointToLocal()
        {
            // Unity 2022.3 arrow probe: +90 deg about +Y takes local +Z to world +X,
            // so its inverse takes +Z to -X. The reference model's host Hamilton
            // algebra produced the same -X; this test asserts Unity's own Quaternion.
            Quaternion yaw = Quaternion.AngleAxis(90f, Vector3.up);
            AssertVector(Vector3.right, yaw * Vector3.forward);

            Vector3 local;
            Assert.IsTrue(RadialProxyMath.TryWorldPointToLocal(new Vector3(0f, 0f, 1f), Vector3.zero, yaw,
                out local));
            AssertVector(new Vector3(-1f, 0f, 0f), local);
        }

        [Test]
        public void WorldPointIsTransformedIntoBallLocalFrame()
        {
            // Ball at (10,0,0) rotated +90 deg about +Y; the world endpoint
            // (10,0,-2) is 2 m along local +X. The best-aligned direction therefore
            // reports 2 * its own x component, and no other direction exceeds it.
            ProxyShell shell = new ProxyShell(0.5f);
            Quaternion yaw = Quaternion.AngleAxis(90f, Vector3.up);
            Vector3 local;
            Assert.IsTrue(RadialProxyMath.TryWorldPointToLocal(new Vector3(10f, 0f, -2f),
                new Vector3(10f, 0f, 0f), yaw, out local));
            AssertVector(new Vector3(2f, 0f, 0f), local);
            shell.Attach(new[] { new Vector3(10f, 0f, -2f) }, new Vector3(10f, 0f, 0f), yaw,
                "transformed#1", false);

            int best = 0;
            for (int i = 1; i < shell.Directions.Length; i++)
            {
                if (shell.Directions[i].x > shell.Directions[best].x) best = i;
            }
            Assert.AreEqual(2f * shell.Directions[best].x, shell.Supports[best], Tolerance);
        }

        [Test]
        public void InvalidAttachAndRebuildAreAtomic()
        {
            ProxyShell shell = new ProxyShell(1f);
            shell.Attach(new[] { new Vector3(0f, 0f, 2f) }, Vector3.zero, Quaternion.identity, "valid#1", false);
            float[] beforeSupports = CopySupports(shell.Supports);
            string[] beforeCandidates = Prefix(shell.CandidateKeys, shell.CandidateCount);

            // A non-finite endpoint is rejected by the batch transform, and the pool is
            // untouched, so the shell keeps its previous supports.
            Vector3[] local = new Vector3[1];
            Assert.IsFalse(RadialProxyMath.TryTransformEndpointsToLocal(
                new[] { new Vector3(float.PositiveInfinity, 0f, 0f) }, 1, Vector3.zero,
                Quaternion.identity, local));
            AssertSupportsEqual(beforeSupports, shell.Supports);

            // A zero quaternion cannot place an endpoint in the ball frame either.
            Assert.IsFalse(RadialProxyMath.TryTransformEndpointsToLocal(
                new[] { new Vector3(1f, 0f, 0f) }, 1, Vector3.zero, new Quaternion(0f, 0f, 0f, 0f), local));
            AssertSupportsEqual(beforeSupports, shell.Supports);

            // A malformed ledger (an item without endpoints) rejects the whole rebuild
            // and leaves the previous supports and candidate keys in place.
            Assert.GreaterOrEqual(shell.ItemCount, 1);
            int savedLength = shell.ItemLengths[0];
            shell.ItemLengths[0] = 0;
            Assert.IsFalse(shell.Rebuild(0.25f));
            shell.ItemLengths[0] = savedLength;
            AssertSupportsEqual(beforeSupports, shell.Supports);
            CollectionAssert.AreEqual(beforeCandidates, Prefix(shell.CandidateKeys, shell.CandidateCount));
        }

        [Test]
        public void NonFiniteTransformsAndBadIndicesAreRejected()
        {
            // 3e38 - (-3e38) overflows a float to infinity: the world-to-local
            // subtraction is rejected instead of returning an infinite offset. (The
            // Python case used 1e308 doubles for the same reason.)
            Vector3 local;
            Assert.IsFalse(RadialProxyMath.TryWorldPointToLocal(new Vector3(3e38f, 0f, 0f),
                new Vector3(-3e38f, 0f, 0f), Quaternion.identity, out local));
            AssertVector(Vector3.zero, local);
            Assert.IsFalse(RadialProxyMath.TryWorldPointToLocal(new Vector3(0f, 0f, 1f), Vector3.zero,
                new Quaternion(0f, 0f, 0f, 0f), out local));

            // Out-of-range direction indices are rejected before any write. The Python
            // case also passed 1.0 as an index, which the C# type system already
            // excludes.
            ProxyShell shell = new ProxyShell(1f);
            float[] beforeSupports = CopySupports(shell.Supports);
            Assert.IsFalse(RadialProxyMath.TrySolveSupports(shell.CoreRadius, shell.Pool, shell.PoolCount,
                shell.Directions, new[] { -1 }, 1, shell.Supports));
            Assert.IsFalse(RadialProxyMath.TrySolveSupports(shell.CoreRadius, shell.Pool, shell.PoolCount,
                shell.Directions, new[] { RadialProxyMath.DirectionCount }, 1, shell.Supports));
            AssertSupportsEqual(beforeSupports, shell.Supports);
        }

        private static int[] AllDirectionIndices()
        {
            int[] indices = new int[RadialProxyMath.DirectionCount];
            for (int i = 0; i < indices.Length; i++) indices[i] = i;
            return indices;
        }

        private static bool Contains(int[] values, int count, int value)
        {
            for (int i = 0; i < count; i++)
            {
                if (values[i] == value) return true;
            }
            return false;
        }

        private static int FirstDirectionBelow(Vector3[] directions, Vector3 reference, float threshold)
        {
            for (int i = 0; i < directions.Length; i++)
            {
                float dot = directions[i].x * reference.x + directions[i].y * reference.y +
                    directions[i].z * reference.z;
                if (dot < threshold) return i;
            }
            return -1;
        }

        private static float[] CopySupports(float[] source)
        {
            float[] copy = new float[source.Length];
            for (int i = 0; i < source.Length; i++) copy[i] = source[i];
            return copy;
        }

        private static string[] Prefix(string[] source, int count)
        {
            string[] copy = new string[count];
            for (int i = 0; i < count; i++) copy[i] = source[i];
            return copy;
        }

        private static void AssertKeys(string[] keys, int count, string[] expected)
        {
            Assert.AreEqual(expected.Length, count, "candidate count");
            for (int i = 0; i < expected.Length; i++)
            {
                Assert.AreEqual(expected[i], keys[i], "candidate " + i);
            }
        }

        private static void AssertSupportsEqual(float[] expected, float[] actual)
        {
            for (int i = 0; i < expected.Length; i++)
            {
                Assert.AreEqual(expected[i], actual[i], Tolerance, "support " + i);
            }
        }

        private static void AssertVector(Vector3 expected, Vector3 actual)
        {
            Assert.AreEqual(expected.x, actual.x, Tolerance, "vector x");
            Assert.AreEqual(expected.y, actual.y, Tolerance, "vector y");
            Assert.AreEqual(expected.z, actual.z, Tolerance, "vector z");
        }
    }
}
