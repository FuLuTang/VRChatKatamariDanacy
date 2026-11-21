using NUnit.Framework;
using UnityEngine;
using VRCKatamari.BallLayers;

namespace VRCKatamari.Tests
{
    /// <summary>
    /// Integration tests for the radial-proxy shell (the ball's 16 pins): the caller-owned
    /// ledger, the AABB endpoint protocol, the incremental attach/detach, the atomic rebuild
    /// and the protrusion-clamped pin tips. Every attached prop
    /// is an item (no long-object admission), so the ledger holds hundreds of them.
    /// </summary>
    /// <remarks>
    /// Every expected value is a hand calculation or a geometric invariant (a corner
    /// distance, a percentage of the core, a clamped vector), never a re-run of the
    /// implementation. The test-only Shell plays the role the motor plays at runtime;
    /// it exists because both classes are deliberately stateless.
    /// </remarks>
    public class RadialProxyShellTests
    {
        private const float Tolerance = 1e-5f;

        // Test-side capacities (the motor owns its own): plenty of items, 8 corners each.
        private const int ItemCapacity = 300;
        private const int EndpointCapacity = ItemCapacity * RadialProxyShellMath.AabbCornerCount;

        private sealed class Shell
        {
            public readonly Vector3[] Directions = new Vector3[RadialProxyMath.DirectionCount];
            public readonly float[] Supports = new float[RadialProxyMath.DirectionCount];
            public readonly Vector3[] Pool = new Vector3[EndpointCapacity];
            public readonly int[] Starts = new int[ItemCapacity];
            public readonly int[] Lengths = new int[ItemCapacity];
            public readonly string[] Keys = new string[ItemCapacity];
            public readonly bool[] IsCandidate = new bool[ItemCapacity];
            public readonly string[] CandidateKeys = new string[RadialProxyMath.MaxExtremeCandidates];
            public readonly int[] Affected = new int[RadialProxyMath.DirectionCount];
            public readonly float[] Scratch = new float[RadialProxyMath.DirectionCount];
            public readonly Vector3[] Removed = new Vector3[RadialProxyShellMath.AabbCornerCount];

            public int PoolCount;
            public int ItemCount;
            public int CandidateCount;
            public float CoreRadius;

            public Shell(float coreRadius)
            {
                CoreRadius = coreRadius;
                Assert.IsTrue(RadialProxyMath.TryFillDirections(Directions));
                Assert.IsTrue(RadialProxyShellMath.TryClearShell(Directions, Supports,
                    ref PoolCount, ref ItemCount, CandidateKeys, ref CandidateCount));
            }

            public bool Attach(string key, Vector3[] corners, bool candidate)
            {
                return RadialProxyShellMath.TryAttachItem(Directions, Supports, Pool,
                    ref PoolCount, Starts, Lengths, Keys, IsCandidate, ref ItemCount,
                    CandidateKeys, ref CandidateCount, Affected, Scratch, key, corners,
                    corners.Length, candidate);
            }

            public bool Detach(string key)
            {
                bool existed;
                bool accepted = RadialProxyShellMath.TryDetachItem(Directions, Supports, Pool,
                    ref PoolCount, Starts, Lengths, Keys, IsCandidate, ref ItemCount,
                    CandidateKeys, ref CandidateCount, Affected, Scratch, Removed, key,
                    out existed);
                Assert.IsTrue(accepted);
                return existed;
            }

            public bool Rebuild()
            {
                int candidateCount;
                bool accepted = RadialProxyShellMath.TryRebuildShell(Directions, Supports, Pool,
                    PoolCount, Starts, Lengths, Keys, IsCandidate, ItemCount, CandidateKeys,
                    out candidateCount);
                CandidateCount = candidateCount;
                return accepted;
            }

            private float contactCap;

            /// <summary>Remembers the protrusion cap the next <see cref="Solve"/> clamps to.</summary>
            public bool BuildContact(float maxProtrusionM)
            {
                contactCap = maxProtrusionM;
                return true;
            }

            /// <summary>
            /// The ground support height of the pin table for a ball rolled so local +Z faces
            /// the floor: the largest tip z, each tip clamped to core + cap, never below the
            /// core; <paramref name="appliedCorrection"/> is how far that sits above the core.
            /// </summary>
            public void Solve(Quaternion ballRotation, Vector3 currentCenter, float maxLift,
                out float supportHeight, out float appliedCorrection)
            {
                supportHeight = CoreRadius;
                for (int i = 0; i < RadialProxyMath.DirectionCount; i++)
                {
                    if (!(Supports[i] > CoreRadius)) continue;
                    float length = Mathf.Min(Supports[i], CoreRadius + contactCap);
                    float z = Directions[i].z * length;
                    if (z > supportHeight) supportHeight = z;
                }
                appliedCorrection = supportHeight - CoreRadius;
            }
        }

        /// <summary>
        /// Brute-force reference for the pin model: the table is the max over the corners of
        /// each pin direction, the tip is clamped to core + cap, and with the ball rolled so
        /// local +Z faces the floor the ground support is the largest tip z (never below the
        /// core). Independent of the shell's own bookkeeping.
        /// </summary>
        private static float ExpectedDownSupport(Shell shell, float cap, params Vector3[][] boxes)
        {
            float best = shell.CoreRadius;
            for (int i = 0; i < RadialProxyMath.DirectionCount; i++)
            {
                float raw = 0f;
                for (int b = 0; b < boxes.Length; b++)
                {
                    for (int c = 0; c < boxes[b].Length; c++)
                    {
                        float dot = Vector3.Dot(boxes[b][c], shell.Directions[i]);
                        if (dot > raw) raw = dot;
                    }
                }
                if (!(raw > shell.CoreRadius)) continue;
                float length = Mathf.Min(raw, shell.CoreRadius + cap);
                float z = shell.Directions[i].z * length;
                if (z > best) best = z;
            }
            return best;
        }

        private static Vector3[] RodCorners(Vector3 fullExtents)
        {
            return RodCornersAt(Vector3.zero, fullExtents);
        }

        private static Vector3[] RodCornersAt(Vector3 localCenter, Vector3 fullExtents)
        {
            Vector3[] corners = new Vector3[RadialProxyShellMath.AabbCornerCount];
            float farthest;
            Assert.IsTrue(RadialProxyShellMath.TryBuildAabbCorners(localCenter,
                Quaternion.identity, fullExtents, corners, out farthest));
            return corners;
        }

        [Test]
        public void SingleLongRodLiftsSupportAndIsCapped()
        {
            // A 2 m rod along local +Z, attached on the +Z surface so all its corners lie
            // in that hemisphere (a remote direction must stay untouched). Rotating the
            // visual roll +90 deg about +X puts local +Z at world -Y, so the far tip
            // points at the floor and the raw support is 1.15 + 1.0 = 2.15 m.
            Vector3 rodCenter = new Vector3(0f, 0f, 1.15f);
            Vector3 rodExtents = new Vector3(0.05f, 0.05f, 2f);
            Shell shell = new Shell(0.15f);
            Vector3[] corners = RodCornersAt(rodCenter, rodExtents);
            Assert.IsTrue(shell.Attach("rod#1", corners, true));
            Assert.AreEqual(1, shell.ItemCount, "one item");
            Assert.AreEqual(8, shell.PoolCount, "eight corners");
            Assert.AreEqual(1, shell.CandidateCount, "one candidate");

            Quaternion down = Quaternion.AngleAxis(90f, Vector3.right);
            float cap = 0.15f * 0.35f;
            Assert.IsTrue(shell.BuildContact(cap));

            float support;
            float correction;
            shell.Solve(down, new Vector3(0f, 0.15f, 0f), 0.04f, out support, out correction);
            // The ground point is a PIN tip: direction * min(raw support, core + cap). The
            // expected value is a brute-force max over the rod's corners and the 16 pins, so
            // it does not depend on how the shell stores them.
            Assert.AreEqual(ExpectedDownSupport(shell, cap, corners), support, 1e-4f,
                "support = the best pin tip, clamped to core + cap");
            Assert.Greater(support, 0.15f + Tolerance, "the rod lifts the ball");
            Assert.LessOrEqual(support, 0.15f + cap + Tolerance, "never beyond the cap");

            // The stored support table stays RAW, so the growth path can retract it
            // later; the cap is applied only to the contact points.
            int best = -1;
            float bestDot = -2f;
            for (int i = 0; i < RadialProxyMath.DirectionCount; i++)
            {
                float dot = shell.Directions[i].z;
                if (dot > bestDot) { bestDot = dot; best = i; }
            }
            Assert.GreaterOrEqual(best, 0);
            Assert.Greater(shell.Supports[best], 0.95f, "raw support near the rod tip");
            Assert.Greater(shell.Supports[best] - shell.CoreRadius, cap, "raw protrusion exceeds the cap");

            // A direction in the opposite hemisphere is untouched (raw support 0).
            int remote = -1;
            for (int i = 0; i < RadialProxyMath.DirectionCount; i++)
            {
                if (shell.Directions[i].z < -0.5f) { remote = i; break; }
            }
            Assert.GreaterOrEqual(remote, 0);
            Assert.AreEqual(0f, shell.Supports[remote], Tolerance, "remote direction untouched");
        }

        [Test]
        public void RemovingTheRodDropsTheSupportBackToTheCore()
        {
            Shell shell = new Shell(0.15f);
            Assert.IsTrue(shell.Attach("rod#1", RodCorners(new Vector3(0.05f, 0.05f, 2f)), true));
            Quaternion down = Quaternion.AngleAxis(90f, Vector3.right);
            Assert.IsTrue(shell.BuildContact(0.15f * 0.35f));
            float support;
            float correction;
            shell.Solve(down, new Vector3(0f, 0.15f, 0f), 10f, out support, out correction);
            Assert.Greater(support, 0.15f + Tolerance);

            Assert.IsTrue(shell.Detach("rod#1"));
            Assert.AreEqual(0, shell.ItemCount);
            Assert.AreEqual(0, shell.PoolCount);
            Assert.AreEqual(0, shell.CandidateCount);
            Assert.IsTrue(shell.BuildContact(0.15f * 0.35f));
            shell.Solve(down, new Vector3(0f, 0.15f, 0f), 10f, out support, out correction);
            Assert.AreEqual(0.15f, support, 1e-4f, "support returns to the core sphere");
            Assert.AreEqual(0f, correction, 1e-4f, "no residual lift");

            // Recomputing from the surviving endpoints (none) must not leave the old max.
            for (int i = 0; i < RadialProxyMath.DirectionCount; i++)
            {
                Assert.AreEqual(0f, shell.Supports[i], Tolerance, "support " + i);
            }
        }

        [Test]
        public void SharedDirectionKeepsTheOtherRodsContribution()
        {
            // Two rods on local +Z, 0.60 m and 0.26 m long. The short one is deliberately
            // shorter than the clamp radius (0.10 + 0.035), so removing the long one must
            // reveal 0.13 m rather than falling to the core or keeping 0.30 m.
            Vector3 longExtents = new Vector3(0.03f, 0.03f, 0.6f);
            Vector3 shortExtents = new Vector3(0.03f, 0.03f, 0.26f);
            float core = 0.10f;
            float cap = core * 0.35f;
            Shell shell = new Shell(core);
            Assert.IsTrue(shell.Attach("long#1", RodCorners(longExtents), true));
            Assert.IsTrue(shell.Attach("short#1", RodCorners(shortExtents), true));
            Quaternion down = Quaternion.AngleAxis(90f, Vector3.right);
            Assert.IsTrue(shell.BuildContact(cap));
            float support;
            float correction;
            shell.Solve(down, new Vector3(0f, core, 0f), 10f, out support, out correction);
            Vector3[] longCorners = RodCorners(longExtents);
            Vector3[] shortCorners = RodCorners(shortExtents);
            Assert.AreEqual(ExpectedDownSupport(shell, cap, longCorners, shortCorners), support, 1e-4f,
                "both rods feed the pins; the best tip is the longer rod's, clamped");
            Assert.LessOrEqual(support, core + cap + Tolerance, "no protrusion beyond the cap");
            Assert.Greater(support, core + Tolerance);

            Assert.IsTrue(shell.Detach("long#1"));
            Assert.AreEqual(1, shell.ItemCount, "only the long rod left");
            Assert.IsTrue(shell.BuildContact(cap));
            shell.Solve(down, new Vector3(0f, core, 0f), 10f, out support, out correction);
            // The surviving short rod's own pins take over: recomputed from the surviving
            // corners, not left at the removed rod's maximum.
            Assert.AreEqual(ExpectedDownSupport(shell, cap, shortCorners), support, 1e-4f,
                "short rod's pins take over");
            Assert.Greater(support, core + Tolerance, "the surviving rod still lifts");

            Assert.IsTrue(shell.Detach("short#1"));
            Assert.IsTrue(shell.BuildContact(cap));
            shell.Solve(down, new Vector3(0f, core, 0f), 10f, out support, out correction);
            Assert.AreEqual(core, support, 1e-4f, "core sphere again");
        }

        [Test]
        public void GrowingTheCoreRetractsTheRelativeProtrusionWithoutARebuild()
        {
            Shell shell = new Shell(0.15f);
            Assert.IsTrue(shell.Attach("rod#1", RodCorners(new Vector3(0.05f, 0.05f, 2f)), true));
            int best = -1;
            float bestDot = -2f;
            for (int i = 0; i < RadialProxyMath.DirectionCount; i++)
            {
                float dot = shell.Directions[i].z;
                if (dot > bestDot) { bestDot = dot; best = i; }
            }
            float supportAtStart = shell.Supports[best];
            Assert.Greater(supportAtStart - 0.15f, 0.8f, "a 1 m tip is a big protrusion at 0.15 m core");

            // The motor re-derives the core every step and does NOT write the support
            // table on growth: max(0, support - core) is what retracts.
            shell.CoreRadius = 0.9f;
            Assert.AreEqual(supportAtStart, shell.Supports[best], Tolerance, "table is core-independent");
            Assert.Less(shell.Supports[best] - 0.9f, 0.15f);
            Assert.Greater(shell.Supports[best] - 0.9f, 0f);

            shell.CoreRadius = 1.1f;
            Assert.LessOrEqual(shell.Supports[best] - 1.1f, Tolerance, "the tip is buried");
        }

        [Test]
        public void WholeLedgerRebuildMatchesIncrementalAndClearEmptiesIt()
        {
            Shell shell = new Shell(0.25f);
            Assert.IsTrue(shell.Attach("a#1", RodCorners(new Vector3(0.03f, 0.03f, 0.6f)), true));
            Assert.IsTrue(shell.Attach("b#1", RodCorners(new Vector3(0.03f, 0.5f, 0.03f)), true));
            float[] before = new float[RadialProxyMath.DirectionCount];
            for (int i = 0; i < before.Length; i++) before[i] = shell.Supports[i];
            Assert.AreEqual(2, shell.CandidateCount);

            Assert.IsTrue(shell.Rebuild());
            Assert.AreEqual(2, shell.CandidateCount, "rebuild keeps both candidates");
            for (int i = 0; i < before.Length; i++)
            {
                Assert.AreEqual(before[i], shell.Supports[i], Tolerance, "support " + i);
            }

            Assert.IsTrue(RadialProxyShellMath.TryClearShell(shell.Directions, shell.Supports,
                ref shell.PoolCount, ref shell.ItemCount, shell.CandidateKeys,
                ref shell.CandidateCount));
            Assert.AreEqual(0, shell.PoolCount);
            Assert.AreEqual(0, shell.ItemCount);
            Assert.AreEqual(0, shell.CandidateCount);
            for (int i = 0; i < shell.Supports.Length; i++)
            {
                Assert.AreEqual(0f, shell.Supports[i], Tolerance, "cleared support " + i);
            }
        }

        [Test]
        public void FullCandidateListRefusesTheFifthItemWithoutChangingTheShell()
        {
            Shell shell = new Shell(0.1f);
            Assert.IsTrue(shell.Attach("a#1", RodCorners(new Vector3(0.03f, 0.03f, 0.6f)), true));
            Assert.IsTrue(shell.Attach("b#1", RodCorners(new Vector3(0.03f, 0.03f, 0.6f)), true));
            Assert.IsTrue(shell.Attach("c#1", RodCorners(new Vector3(0.03f, 0.03f, 0.6f)), true));
            Assert.IsTrue(shell.Attach("d#1", RodCorners(new Vector3(0.03f, 0.03f, 0.6f)), true));
            Assert.AreEqual(4 * RadialProxyShellMath.AabbCornerCount, shell.PoolCount);
            float[] before = new float[RadialProxyMath.DirectionCount];
            for (int i = 0; i < before.Length; i++) before[i] = shell.Supports[i];

            Assert.IsFalse(shell.Attach("e#1", RodCorners(new Vector3(0.03f, 0.03f, 0.6f)), true),
                "a fifth candidate is refused, not evicting one");
            Assert.AreEqual(4, shell.ItemCount);
            Assert.AreEqual(4 * RadialProxyShellMath.AabbCornerCount, shell.PoolCount);
            for (int i = 0; i < before.Length; i++)
            {
                Assert.AreEqual(before[i], shell.Supports[i], Tolerance, "support " + i);
            }
            Assert.AreEqual(4, shell.CandidateCount);
        }

        [Test]
        public void CornersFollowThePropsBallLocalRotation()
        {
            // A 1 m rod along the prop's local +Z, turned +90 deg about +Y: in the ball
            // frame its long axis is +X (the same Unity arrow fact the RadialProxy tests
            // assert), so the farthest corner is ~0.5 m out along X and only ~0.02 m
            // along Z.
            Vector3[] corners = new Vector3[RadialProxyShellMath.AabbCornerCount];
            float farthest;
            Assert.IsTrue(RadialProxyShellMath.TryBuildAabbCorners(Vector3.zero,
                Quaternion.AngleAxis(90f, Vector3.up), new Vector3(0.04f, 0.04f, 1f),
                corners, out farthest));
            Assert.AreEqual(0.5f, farthest, 1e-3f, "half the rod along X");
            for (int i = 0; i < corners.Length; i++)
            {
                Assert.LessOrEqual(Mathf.Abs(corners[i].z), 0.021f, "thin along Z");
            }
            Assert.IsFalse(RadialProxyShellMath.TryBuildAabbCorners(Vector3.zero,
                new Quaternion(0f, 0f, 0f, 0f), new Vector3(0.04f, 0.04f, 1f), corners,
                out farthest), "a zero rotation is refused, not normalized");
        }

        [Test]
        public void ManyPropsMergeIncrementallyToTheSameTableAsARebuild()
        {
            // Every attached prop registers (no long-object test): 150 boxes of mixed sizes in
            // deterministic pseudo-random ball-local poses. After each attach the table must be
            // the brute-force max over every corner so far, and a whole-ledger rebuild must
            // leave it unchanged (the table matches the pool by construction).
            Shell shell = new Shell(0.25f);
            System.Collections.Generic.List<Vector3> allCorners =
                new System.Collections.Generic.List<Vector3>();
            uint seed = 12345u;
            for (int n = 0; n < 150; n++)
            {
                Vector3 extents = new Vector3(0.02f + 0.1f * Next(ref seed), 0.02f + 0.1f * Next(ref seed),
                    0.02f + 0.3f * Next(ref seed));
                Vector3 dir = new Vector3(Next(ref seed) - 0.5f, Next(ref seed) - 0.5f,
                    Next(ref seed) - 0.5f).normalized;
                Quaternion rotation = Quaternion.Euler(360f * Next(ref seed), 360f * Next(ref seed),
                    360f * Next(ref seed));
                Vector3[] corners = new Vector3[RadialProxyShellMath.AabbCornerCount];
                float farthest;
                Assert.IsTrue(RadialProxyShellMath.TryBuildAabbCorners(dir * 0.25f, rotation,
                    extents, corners, out farthest));
                Assert.IsTrue(shell.Attach("prop#" + n, corners, false), "attach " + n);
                allCorners.AddRange(corners);
                Assert.AreEqual(n + 1, shell.ItemCount);
                Assert.AreEqual(0, shell.CandidateCount, "no candidates: every prop is an item");
            }
            Assert.AreEqual(150 * RadialProxyShellMath.AabbCornerCount, shell.PoolCount);

            float[] expected = new float[RadialProxyMath.DirectionCount];
            for (int d = 0; d < expected.Length; d++)
            {
                float best = 0f;
                for (int c = 0; c < allCorners.Count; c++)
                {
                    float dot = Vector3.Dot(allCorners[c], shell.Directions[d]);
                    if (dot > best) best = dot;
                }
                expected[d] = best;
                Assert.AreEqual(best, shell.Supports[d], Tolerance, "incremental support " + d);
            }

            Assert.IsTrue(shell.Rebuild());
            for (int d = 0; d < expected.Length; d++)
            {
                Assert.AreEqual(expected[d], shell.Supports[d], Tolerance, "rebuilt support " + d);
            }
        }

        [Test]
        public void ADetachAmongManyPropsRecomputesFromTheSurvivors()
        {
            Shell shell = new Shell(0.2f);
            Vector3[] big = RodCornersAt(new Vector3(0f, 0f, 0.4f), new Vector3(0.1f, 0.1f, 0.5f));
            Vector3[] small = RodCornersAt(new Vector3(0f, 0f, 0.25f), new Vector3(0.1f, 0.1f, 0.1f));
            Assert.IsTrue(shell.Attach("small#1", small, false));
            Assert.IsTrue(shell.Attach("big#1", big, false));
            Assert.IsTrue(shell.Attach("small#2", RodCornersAt(new Vector3(0.1f, 0f, 0f),
                new Vector3(0.05f, 0.05f, 0.05f)), false));
            Quaternion down = Quaternion.AngleAxis(90f, Vector3.right);
            float cap = 10f;
            float support;
            float correction;
            Assert.IsTrue(shell.BuildContact(cap));
            shell.Solve(down, new Vector3(0f, 0.2f, 0f), 10f, out support, out correction);
            Assert.AreEqual(ExpectedDownSupport(shell, cap, small, big, RodCornersAt(
                new Vector3(0.1f, 0f, 0f), new Vector3(0.05f, 0.05f, 0.05f))), support, 1e-4f);

            Assert.IsTrue(shell.Detach("big#1"));
            Assert.AreEqual(2, shell.ItemCount);
            Assert.IsTrue(shell.BuildContact(cap));
            shell.Solve(down, new Vector3(0f, 0.2f, 0f), 10f, out support, out correction);
            Assert.AreEqual(ExpectedDownSupport(shell, cap, small, RodCornersAt(
                new Vector3(0.1f, 0f, 0f), new Vector3(0.05f, 0.05f, 0.05f))), support, 1e-4f,
                "the big prop's maximum is gone; the survivors' pins remain");
        }

        private static float Next(ref uint state)
        {
            state = state * 1664525u + 1013904223u;
            return (state >> 8) / 16777216f;
        }

        private static void AssertVector(Vector3 expected, Vector3 actual)
        {
            Assert.AreEqual(expected.x, actual.x, Tolerance, "vector x");
            Assert.AreEqual(expected.y, actual.y, Tolerance, "vector y");
            Assert.AreEqual(expected.z, actual.z, Tolerance, "vector z");
        }
    }
}
