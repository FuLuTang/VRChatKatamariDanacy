using NUnit.Framework;
using UnityEngine;
using VRCKatamari.BallLayers;

namespace VRCKatamari.Tests
{
    /// <summary>
    /// Integration tests for the radial-proxy shell: the caller-owned ledger, the
    /// AABB endpoint protocol, the incremental attach/detach, the atomic rebuild and
    /// the protrusion-clamped points that drive GroundContactMath.
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

        private sealed class Shell
        {
            public readonly Vector3[] Directions = new Vector3[RadialProxyMath.DirectionCount];
            public readonly float[] Supports = new float[RadialProxyMath.DirectionCount];
            public readonly Vector3[] Pool = new Vector3[RadialProxyShellMath.EndpointCapacity];
            public readonly Vector3[] Contact = new Vector3[RadialProxyShellMath.EndpointCapacity];
            public readonly int[] Starts = new int[RadialProxyShellMath.ItemCapacity];
            public readonly int[] Lengths = new int[RadialProxyShellMath.ItemCapacity];
            public readonly string[] Keys = new string[RadialProxyShellMath.ItemCapacity];
            public readonly bool[] IsCandidate = new bool[RadialProxyShellMath.ItemCapacity];
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

            public bool BuildContact(float maxProtrusionM)
            {
                return RadialProxyShellMath.TryBuildContactPoints(CoreRadius, maxProtrusionM,
                    Pool, PoolCount, Contact);
            }

            /// <summary>Ground support height and the applied correction for this pose.</summary>
            public void Solve(Quaternion ballRotation, Vector3 currentCenter, float maxLift,
                out float supportHeight, out float appliedCorrection)
            {
                Vector3 corrected;
                float coordinate;
                Vector3 anchor;
                float perturbation;
                bool liftClamped;
                bool dropClamped;
                bool accepted = GroundContactMath.TrySolve(CoreRadius, ballRotation, Contact,
                    Vector3.up, 0f, currentCenter, maxLift, 10f, 0f, 0f,
                    out supportHeight, out corrected, out coordinate, out anchor,
                    out appliedCorrection, out perturbation, out liftClamped, out dropClamped);
                Assert.IsTrue(accepted);
            }
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
            // The clamp is radial (README "夹紧是径向的"): an endpoint longer than core+cap
            // is scaled along its own ray to that radius, so the along-axis support is the
            // projection (core+cap) * tipZ / |tip|, not the bare core+cap. Here the cross
            // section is thin, so the two differ by only ~2.7e-5 m, but the invariant is
            // the projection.
            float halfX = rodExtents.x * 0.5f;
            float halfY = rodExtents.y * 0.5f;
            float tipZ = rodCenter.z + rodExtents.z * 0.5f;
            float tipLength = Mathf.Sqrt(halfX * halfX + halfY * halfY + tipZ * tipZ);
            Assert.AreEqual((0.15f + cap) * (tipZ / tipLength), support, 1e-4f,
                "support = (core + cap) projected along the tip direction");
            Assert.LessOrEqual(support, 0.15f + cap, "never beyond the radial clamp");
            Assert.AreEqual(0.04f, correction, 1e-4f, "one-step lift is clamped");
            Assert.LessOrEqual(support - 0.15f, cap + Tolerance, "no protrusion beyond the cap");

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
            // The clamp is radial (README "夹紧是径向的"): an endpoint longer than core+cap
            // keeps its direction and is scaled to that radius, so a tip slightly off the
            // axis contributes only the along-axis projection. The long rod's far corner
            // is (0.015, 0.015, 0.30), so the expected support is
            //   (core+cap) * hz / |corner| = 0.135 * 0.30 / sqrt(0.015^2+0.015^2+0.30^2)
            //                               = 0.135 * cos(4.045 deg) = 0.134664 m,
            // NOT the full clamp radius 0.135 m (that would be the support of a tip
            // exactly on the axis).
            float halfX = longExtents.x * 0.5f;
            float halfY = longExtents.y * 0.5f;
            float halfZ = longExtents.z * 0.5f;
            float tipLength = Mathf.Sqrt(halfX * halfX + halfY * halfY + halfZ * halfZ);
            float clampedSupport = (core + cap) * (halfZ / tipLength);
            Assert.AreEqual(clampedSupport, support, 1e-4f,
                "radial clamp keeps (core + cap) * cos(tip tilt) along the axis, not the bare cap");
            Assert.Less(support, core + cap, "a tilted tip cannot reach the bare clamp radius");

            Assert.IsTrue(shell.Detach("long#1"));
            Assert.AreEqual(1, shell.ItemCount, "only the long rod left");
            Assert.IsTrue(shell.BuildContact(cap));
            shell.Solve(down, new Vector3(0f, core, 0f), 10f, out support, out correction);
            // The short tip's corner diagonal (0.131719 m) is still inside the clamp
            // radius, so it is not shortened at all and support is its bare hz = 0.13 m.
            Assert.AreEqual(shortExtents.z * 0.5f, support, 1e-4f, "short rod's tip takes over");
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
            Assert.AreEqual(RadialProxyShellMath.EndpointCapacity, shell.PoolCount);
            float[] before = new float[RadialProxyMath.DirectionCount];
            for (int i = 0; i < before.Length; i++) before[i] = shell.Supports[i];

            Assert.IsFalse(shell.Attach("e#1", RodCorners(new Vector3(0.03f, 0.03f, 0.6f)), true),
                "a fifth candidate is refused, not evicting one");
            Assert.AreEqual(4, shell.ItemCount);
            Assert.AreEqual(RadialProxyShellMath.EndpointCapacity, shell.PoolCount);
            for (int i = 0; i < before.Length; i++)
            {
                Assert.AreEqual(before[i], shell.Supports[i], Tolerance, "support " + i);
            }
            Assert.AreEqual(4, shell.CandidateCount);
        }

        [Test]
        public void LongObjectAdmissionUsesLengthAndSecondLongestEdge()
        {
            bool isLong;
            Assert.IsTrue(RadialProxyShellMath.TryClassifyLongObject(
                new Vector3(0.03f, 0.02f, 0.18f), 0.1f, 2.5f, 0.5f, out isLong));
            Assert.IsTrue(isLong, "a spoon is a long object at a 0.1 m core");

            // The real StoryLevel banana row: 0.18 x 0.068 x 0.032 (second longest 0.068).
            Assert.IsTrue(RadialProxyShellMath.TryClassifyLongObject(
                new Vector3(0.18f, 0.032f, 0.068f), 0.15f, 2.5f, 0.5f, out isLong));
            Assert.IsTrue(isLong, "the banana bounds pass at the starting ball");

            // A square sheet with one thin axis is NOT a long object: longest/second = 1.
            Assert.IsTrue(RadialProxyShellMath.TryClassifyLongObject(
                new Vector3(0.3f, 0.028f, 0.3f), 0.15f, 2.5f, 0.5f, out isLong));
            Assert.IsFalse(isLong, "a napkin sheet is wide in two axes");

            Assert.IsTrue(RadialProxyShellMath.TryClassifyLongObject(
                new Vector3(0.08f, 0.08f, 0.1f), 0.1f, 2.5f, 0.5f, out isLong));
            Assert.IsFalse(isLong, "a cup is not elongated");

            Assert.IsTrue(RadialProxyShellMath.TryClassifyLongObject(
                new Vector3(0.02f, 0.02f, 0.08f), 0.5f, 2.5f, 0.5f, out isLong));
            Assert.IsFalse(isLong, "too short relative to the ball");

            Assert.IsTrue(RadialProxyShellMath.TryClassifyLongObject(
                new Vector3(0.2f, 0.005f, 1f), 0.3f, 2.5f, 0.5f, out isLong));
            Assert.IsTrue(isLong, "a flat plank still qualifies");

            Assert.IsFalse(RadialProxyShellMath.TryClassifyLongObject(
                new Vector3(-0.1f, 0.02f, 0.3f), 0.1f, 2.5f, 0.5f, out isLong));
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
        public void ContactPointsClampLongEndpointsAndZeroUnusedSlots()
        {
            Vector3[] endpoints = new Vector3[RadialProxyShellMath.EndpointCapacity];
            Vector3[] contact = new Vector3[RadialProxyShellMath.EndpointCapacity];
            endpoints[0] = new Vector3(0f, 0f, 2f);
            endpoints[1] = new Vector3(0f, 0f, 0.1f);
            Assert.IsTrue(RadialProxyShellMath.TryBuildContactPoints(0.5f, 0.1f, endpoints, 2,
                contact));
            AssertVector(new Vector3(0f, 0f, 0.6f), contact[0]);
            AssertVector(new Vector3(0f, 0f, 0.1f), contact[1]);
            for (int i = 2; i < contact.Length; i++)
            {
                AssertVector(Vector3.zero, contact[i]);
            }
            Assert.IsFalse(RadialProxyShellMath.TryBuildContactPoints(0.5f, 0.1f, endpoints, 2,
                new Vector3[RadialProxyShellMath.EndpointCapacity - 1]));
        }

        private static void AssertVector(Vector3 expected, Vector3 actual)
        {
            Assert.AreEqual(expected.x, actual.x, Tolerance, "vector x");
            Assert.AreEqual(expected.y, actual.y, Tolerance, "vector y");
            Assert.AreEqual(expected.z, actual.z, Tolerance, "vector z");
        }
    }
}
