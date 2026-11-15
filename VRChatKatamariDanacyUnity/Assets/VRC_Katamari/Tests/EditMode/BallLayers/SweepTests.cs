using NUnit.Framework;
using UnityEngine;
using VRCKatamari.BallLayers;

namespace VRCKatamari.Tests
{
    /// <summary>
    /// EditMode port of Docs/tests/ball_sweep/test_sweep.py. Lengths are metres
    /// in world space (+Y up). Expected values are hand calculations or
    /// geometric invariants; no assertion re-runs the implementation formula.
    /// The Python cases used doubles and a 1e-8 default separation epsilon; the
    /// port pins separationEpsilon to 1e-5 m so a real push is visible above
    /// float resolution, and widens position comparisons to 1e-4 m.
    ///
    /// The wall-response cases (contact normals turned outward, then the inward
    /// planar velocity removed) reuse this file's sweep so the normals under test
    /// are the ones the motor actually consumes, including the box normal that
    /// the sweep reports with the reference's inner-face sign.
    /// </summary>
    public class SweepTests
    {
        private const float Tolerance = 1e-4f;

        // Above the internal 1e-6 m epsilon, below anything the tests measure.
        private const float SeparationEpsilon = 1e-5f;

        // Test-only view of the parallel contact arrays; the runtime API has no
        // struct for them.
        private sealed class ContactRecord
        {
            public float Time;
            public Vector3 Center;
            public Vector3 Point;
            public Vector3 Normal;
            public int ObstacleIndex;
            public float PenetrationDepth;
        }

        private sealed class SweepResult
        {
            public bool Accepted;
            public Vector3 Position;
            public Vector3 Remaining;
            public Vector3 Slide;
            public ContactRecord[] Contacts;
            public Vector3[] ContactCenters;
            public Vector3[] ContactNormals;
            public int[] ContactObstacleIndices;
            public bool Stuck;
            public bool Blocked;
        }

        [Test]
        public void HighSpeedDoesNotCrossThinWall()
        {
            // Box x in [0, 0.1] expanded by r = 0.5 has its -X face at -0.5, so a
            // 100 m step still stops the center on that face instead of tunnelling.
            Vector3[] minima = { new Vector3(0f, -2f, -2f) };
            Vector3[] maxima = { new Vector3(0.1f, 2f, 2f) };
            SweepResult result = Sweep(new Vector3(-20f, 0f, 0f), new Vector3(100f, 0f, 0f),
                0.5f, null, null, minima, maxima);
            Assert.IsTrue(result.Accepted);
            Assert.AreEqual(-0.5f, result.Position.x, Tolerance);
            Assert.Greater(result.Contacts.Length, 0);
            Assert.LessOrEqual(result.Position.x, -0.5f + Tolerance);
            Assert.IsTrue(result.Blocked);
            Assert.IsFalse(result.Stuck);
        }

        [Test]
        public void WallThinnerThanRadiusIsNotTunnelled()
        {
            // A 0.05 m wall inside a 1 m sphere: the expanded -X face is at
            // 5 - 1 = 4 m, and that face's free-side normal points along -X.
            Vector3[] minima = { new Vector3(5f, -1f, -1f) };
            Vector3[] maxima = { new Vector3(5.05f, 1f, 1f) };
            SweepResult result = Sweep(new Vector3(0f, 0f, 0f), new Vector3(12f, 0f, 0f),
                1f, null, null, minima, maxima);
            Assert.IsTrue(result.Accepted);
            Assert.AreEqual(4f, result.Position.x, Tolerance);
            Assert.Greater(result.Contacts.Length, 0);
            Assert.AreEqual(-1f, result.Contacts[0].Normal.x, 0f);
            Assert.AreEqual(0f, result.Contacts[0].Normal.y, 0f);
            Assert.AreEqual(0f, result.Contacts[0].Normal.z, 0f);
        }

        [Test]
        public void CornerProjectsAndSlidesOut()
        {
            // Two half-spaces x >= 0 and y >= 0 with zero radius: both are reached
            // at t = 1/2 while the +Z motion is untouched, so the sphere slides
            // along the corner to z = 3 without being blocked.
            Vector3[] normals = { new Vector3(1f, 0f, 0f), new Vector3(0f, 1f, 0f) };
            float[] offsets = { 0f, 0f };
            SweepResult result = Sweep(new Vector3(1f, 1f, 0f), new Vector3(-2f, -2f, 3f),
                0f, normals, offsets);
            Assert.IsTrue(result.Accepted);
            Assert.GreaterOrEqual(result.Position.x, -Tolerance);
            Assert.GreaterOrEqual(result.Position.y, -Tolerance);
            Assert.AreEqual(3f, result.Position.z, Tolerance);
            Assert.IsFalse(result.Stuck);
            Assert.IsFalse(result.Blocked);
        }

        [Test]
        public void MultipleContactsHaveValidOrderAndTime()
        {
            // y = 2 - 4t reaches the y plane at t = 1/2; the remaining x motion is
            // -1.5, which needs another 1/3 of the residual step, so the x plane is
            // reached at 1/2 + (1/2)(1/3) = 2/3. Input order is x then y, so the
            // recorded order proves sorting by time rather than by obstacle index.
            Vector3[] normals = { new Vector3(1f, 0f, 0f), new Vector3(0f, 1f, 0f) };
            float[] offsets = { 0f, 0f };
            SweepResult result = Sweep(new Vector3(2f, 2f, 0f), new Vector3(-3f, -4f, 0f),
                0f, normals, offsets);
            Assert.IsTrue(result.Accepted);
            Assert.AreEqual(2, result.Contacts.Length);
            Assert.AreEqual(1, result.Contacts[0].ObstacleIndex);
            Assert.AreEqual(0, result.Contacts[1].ObstacleIndex);
            Assert.AreEqual(0.5f, result.Contacts[0].Time, Tolerance);
            Assert.AreEqual(2f / 3f, result.Contacts[1].Time, Tolerance);
            Assert.IsFalse(result.Stuck);
        }

        [Test]
        public void FullSingleWallBlockIsBlockedNotStuck()
        {
            // Head-on stop: the contact consumes the whole -X request, which is a
            // clean block, not a solver failure.
            Vector3[] normals = { new Vector3(1f, 0f, 0f) };
            float[] offsets = { 0f };
            SweepResult result = Sweep(new Vector3(2f, 0f, 0f), new Vector3(-3f, 0f, 0f),
                0.5f, normals, offsets);
            Assert.IsTrue(result.Accepted);
            Assert.AreEqual(0.5f, result.Position.x, Tolerance);
            Assert.AreEqual(0f, result.Remaining.x, 0f);
            Assert.AreEqual(0f, result.Remaining.y, 0f);
            Assert.AreEqual(0f, result.Remaining.z, 0f);
            Assert.IsTrue(result.Blocked);
            Assert.IsFalse(result.Stuck);
        }

        [Test]
        public void ZeroVelocityAndTangentStartDoNotJitter()
        {
            // A sphere already touching a wall at x = 0 with r = 1 and no inward
            // motion is not in contact: zero displacement and pure tangent motion
            // both stay free.
            Vector3[] normals = { new Vector3(1f, 0f, 0f) };
            float[] offsets = { 0f };

            SweepResult stationary = Sweep(new Vector3(1f, 0f, 0f), Vector3.zero,
                1f, normals, offsets);
            Assert.IsTrue(stationary.Accepted);
            AssertVector(new Vector3(1f, 0f, 0f), stationary.Position);
            Assert.AreEqual(0, stationary.Contacts.Length);

            SweepResult tangent = Sweep(new Vector3(1f, 0f, 0f), new Vector3(0f, 0f, 2f),
                1f, normals, offsets);
            Assert.IsTrue(tangent.Accepted);
            Assert.AreEqual(2f, tangent.Position.z, 0f);
            Assert.AreEqual(0, tangent.Contacts.Length);
            Assert.IsFalse(tangent.Stuck);
        }

        [Test]
        public void ZeroDisplacementInitialPlaneEmbeddingIsRecovered()
        {
            // The center starts 0.25 m inside the free face of a 0.5 m sphere; the
            // recovery must use that real depth, not just the separation epsilon.
            Vector3[] normals = { new Vector3(1f, 0f, 0f) };
            float[] offsets = { 0f };
            SweepResult result = Sweep(new Vector3(0.25f, 0f, 0f), Vector3.zero,
                0.5f, normals, offsets);
            Assert.IsTrue(result.Accepted);
            Assert.AreEqual(1, result.Contacts.Length);
            Assert.AreEqual(0.25f, result.Contacts[0].PenetrationDepth, Tolerance);
            Assert.GreaterOrEqual(result.Position.x, 0.5f);
            Assert.Greater(result.Position.x - 0.5f, 0f);
            Assert.IsFalse(result.Stuck);
            Assert.IsFalse(result.Blocked);
        }

        [Test]
        public void BoxInitialEmbeddingUsesRealExpandedFaceDepth()
        {
            // Box [-1,1]^3 expanded by r = 0.5 has faces at +-1.5; the center at the
            // origin is 1.5 m from every face, and the negative-X face wins the
            // deterministic tie.
            Vector3[] minima = { new Vector3(-1f, -1f, -1f) };
            Vector3[] maxima = { new Vector3(1f, 1f, 1f) };
            SweepResult result = Sweep(Vector3.zero, Vector3.zero, 0.5f,
                null, null, minima, maxima);
            Assert.IsTrue(result.Accepted);
            Assert.AreEqual(1, result.Contacts.Length);
            Assert.AreEqual(1.5f, result.Contacts[0].PenetrationDepth, Tolerance);
            Assert.LessOrEqual(result.Position.x, -1.5f);
            Assert.AreEqual(0f, result.Position.y, 0f);
            Assert.IsFalse(result.Stuck);
        }

        [Test]
        public void BoxAsymmetricEmbeddingPushesToNearestFreeFace()
        {
            // Expanded box is [-1.5,1.5]^3. (0.25,0.8,0) is 0.7 m inside the +Y
            // face but 1.25 m inside +X, so +Y is the nearest free face.
            Vector3[] minima = { new Vector3(-1f, -1f, -1f) };
            Vector3[] maxima = { new Vector3(1f, 1f, 1f) };
            SweepResult result = Sweep(new Vector3(0.25f, 0.8f, 0f), Vector3.zero,
                0.5f, null, null, minima, maxima);
            Assert.IsTrue(result.Accepted);
            Assert.AreEqual(0.7f, result.Contacts[0].PenetrationDepth, Tolerance);
            Assert.GreaterOrEqual(result.Position.y, 1.5f);
            Assert.IsFalse(result.Stuck);
        }

        [Test]
        public void ExpandedAabbCornerIsConservativeNotExactSphereBox()
        {
            // The exact rounded-box gap at (-0.4,-0.4,0.5) is hypot(0.4,0.4) =
            // 0.566 m, more than r = 0.5, so a true sphere-box test would miss.
            // The square expansion reports a contact on the -X face instead; that
            // early contact is the documented conservative approximation.
            Assert.Greater(Mathf.Sqrt(0.4f * 0.4f + 0.4f * 0.4f), 0.5f);
            Vector3[] minima = { new Vector3(0f, 0f, 0f) };
            Vector3[] maxima = { new Vector3(1f, 1f, 1f) };
            SweepResult result = Sweep(new Vector3(-0.4f, -0.4f, 0.5f), Vector3.zero,
                0.5f, null, null, minima, maxima);
            Assert.IsTrue(result.Accepted);
            Assert.Greater(result.Contacts.Length, 0);
            Assert.IsFalse(result.Stuck);
        }

        [Test]
        public void ContactBudgetExhaustionIsExplicitAndKeepsResidual()
        {
            // One contact is allowed; after the y plane the -X residual is still
            // unsolved, so the solver reports stuck and hands that residual back.
            Vector3[] normals = { new Vector3(1f, 0f, 0f), new Vector3(0f, 1f, 0f) };
            float[] offsets = { 0f, 0f };
            SweepResult result = Sweep(new Vector3(2f, 2f, 0f), new Vector3(-3f, -4f, 0f),
                0f, normals, offsets, null, null, 1);
            Assert.IsTrue(result.Accepted);
            Assert.IsTrue(result.Stuck);
            Assert.Greater(result.Remaining.magnitude, 0f);
            Assert.AreEqual(-1.5f, result.Remaining.x, Tolerance);
            Assert.AreEqual(0f, result.Remaining.y, Tolerance);
        }

        [Test]
        public void SameInputsAreDeterministic()
        {
            Vector3[] normals = { new Vector3(1f, 0f, 0f) };
            float[] offsets = { 0f };
            Vector3[] minima = { new Vector3(0f, 3f, -1f) };
            Vector3[] maxima = { new Vector3(0.2f, 4f, 1f) };
            SweepResult first = Sweep(new Vector3(2f, 2f, 0f), new Vector3(-4f, 3f, 0f),
                0.25f, normals, offsets, minima, maxima);
            SweepResult second = Sweep(new Vector3(2f, 2f, 0f), new Vector3(-4f, 3f, 0f),
                0.25f, normals, offsets, minima, maxima);
            AssertSameResult(first, second);
        }

        [Test]
        public void NonUnitPlaneScalesItsOffsetTogether()
        {
            // dot((0,2,0), x) >= 4 normalizes to y >= 2; a 0.5 m sphere then sits at
            // y = 2.5. Only the pair normalization can produce that value.
            Vector3[] normals = { new Vector3(0f, 2f, 0f) };
            float[] offsets = { 4f };
            SweepResult result = Sweep(Vector3.zero, Vector3.zero, 0.5f, normals, offsets);
            Assert.IsTrue(result.Accepted);
            Assert.AreEqual(2.5f, result.Position.y, Tolerance);
        }

        [Test]
        public void InvalidOrNonFiniteGeometryIsRejected()
        {
            // The Python 2-component start has no C# analogue; NaN stands in for
            // the non-finite shape case. The Python 1.1e-10 sweep displacement
            // that overflowed a double TOI is below this port's 1e-6 float
            // epsilon, so a 1e-3 m displacement is used to overflow the TOI
            // division instead.
            Assert.IsFalse(Sweep(new Vector3(float.NaN, 0f, 0f), Vector3.right, 1f,
                null, null).Accepted);
            Assert.IsFalse(Sweep(Vector3.zero, Vector3.zero, -1f, null, null).Accepted);
            Assert.IsFalse(Sweep(Vector3.zero, Vector3.zero, 1f, null, null,
                new[] { new Vector3(1f, 0f, 0f) }, new[] { new Vector3(0f, 1f, 1f) }).Accepted);
            Assert.IsFalse(Sweep(Vector3.zero, Vector3.zero, 1f,
                new[] { Vector3.zero }, new[] { 0f }).Accepted);
            Assert.IsFalse(Sweep(new Vector3(3e38f, 0f, 0f), new Vector3(3e38f, 0f, 0f),
                1f, null, null).Accepted);
            Assert.IsFalse(Sweep(new Vector3(3e38f, 0f, 0f), new Vector3(-1e-3f, 0f, 0f),
                0f, null, null,
                new[] { new Vector3(-1f, -1f, -1f) },
                new[] { new Vector3(1f, 1f, 1f) }).Accepted);
            Assert.IsFalse(Sweep(Vector3.zero, Vector3.zero, 1f, null, null,
                null, null, 0).Accepted);
        }

        [Test]
        public void RejectedCallLeavesEveryOutputZeroAndNoContacts()
        {
            SweepResult result = Sweep(Vector3.zero, Vector3.zero, 1f,
                new[] { Vector3.zero }, new[] { 0f });
            Assert.IsFalse(result.Accepted);
            AssertVector(Vector3.zero, result.Position);
            AssertVector(Vector3.zero, result.Remaining);
            AssertVector(Vector3.zero, result.Slide);
            Assert.AreEqual(0, result.Contacts.Length);
            Assert.IsFalse(result.Stuck);
            Assert.IsFalse(result.Blocked);
        }

        [Test]
        public void HeadOnWallContactRemovesTheWholeNormalSpeed()
        {
            // The wall is the half-space x >= 0, so its outward normal is +X. A
            // 3.95 m/s drive straight into it loses that speed completely and
            // keeps nothing tangential.
            Vector3[] normals = { new Vector3(1f, 0f, 0f) };
            float[] offsets = { 0f };
            SweepResult sweep = Sweep(new Vector3(2f, 0f, 0f), new Vector3(-3f, 0f, 0f),
                0.5f, normals, offsets);
            Assert.IsTrue(sweep.Blocked);
            Assert.AreEqual(0.5f, sweep.Position.x, Tolerance);

            Vector3[] wallNormals = new Vector3[4];
            int wallNormalCount;
            Assert.IsTrue(CollectWallNormals(sweep, normals, offsets, null, null,
                wallNormals, out wallNormalCount));
            Assert.AreEqual(1, wallNormalCount);
            AssertVector(new Vector3(1f, 0f, 0f), wallNormals[0]);

            Vector3 corrected;
            Assert.IsTrue(SweepMath.TryRemoveWallNormalVelocity(
                new Vector3(-3.95f, 0f, 0f), wallNormals, wallNormalCount, out corrected));
            AssertVector(Vector3.zero, corrected);
        }

        [Test]
        public void GlancingHitKeepsTheTangentialSpeed()
        {
            // A 45 degree approach: 2 m/s into the wall and 2 m/s along it. Only
            // the inward X part is removed, so the ball ends up sliding along -Z
            // at the full 2 m/s instead of stopping at the wall.
            Vector3[] normals = { new Vector3(1f, 0f, 0f) };
            float[] offsets = { 0f };
            SweepResult sweep = Sweep(new Vector3(2f, 0f, 0f), new Vector3(-3f, 0f, -3f),
                0.5f, normals, offsets);
            Assert.AreEqual(0.5f, sweep.Position.x, Tolerance);
            Assert.AreEqual(-3f, sweep.Position.z, Tolerance);
            Assert.AreEqual(1, sweep.Contacts.Length);
            Assert.AreEqual(1f, sweep.Contacts[0].Normal.x, 0f);

            Vector3[] wallNormals = new Vector3[4];
            int wallNormalCount;
            Assert.IsTrue(CollectWallNormals(sweep, normals, offsets, null, null,
                wallNormals, out wallNormalCount));
            Assert.AreEqual(1, wallNormalCount);

            Vector3 corrected;
            Assert.IsTrue(SweepMath.TryRemoveWallNormalVelocity(
                new Vector3(-2f, 0f, -2f), wallNormals, wallNormalCount, out corrected));
            Assert.AreEqual(0f, corrected.x, Tolerance);
            Assert.AreEqual(0f, corrected.y, 0f);
            Assert.AreEqual(-2f, corrected.z, Tolerance);
        }

        [Test]
        public void MovingAwayFromAWallLeavesVelocityUnchanged()
        {
            // Driving away from the wall records no contact at all, and a wall
            // normal with a positive dot product is a no-op even if one is
            // supplied: the rule only ever removes inward motion.
            Vector3[] normals = { new Vector3(1f, 0f, 0f) };
            float[] offsets = { 0f };
            SweepResult sweep = Sweep(new Vector3(2f, 0f, 0f), new Vector3(3f, 0f, 0f),
                0.5f, normals, offsets);
            Assert.AreEqual(0, sweep.Contacts.Length);

            Vector3[] wallNormals = new Vector3[4];
            int wallNormalCount;
            Assert.IsTrue(CollectWallNormals(sweep, normals, offsets, null, null,
                wallNormals, out wallNormalCount));
            Assert.AreEqual(0, wallNormalCount);

            Vector3 corrected;
            Assert.IsTrue(SweepMath.TryRemoveWallNormalVelocity(
                new Vector3(3f, 0f, 0f), wallNormals, wallNormalCount, out corrected));
            AssertVector(new Vector3(3f, 0f, 0f), corrected);

            Vector3[] outward = { new Vector3(1f, 0f, 0f) };
            Assert.IsTrue(SweepMath.TryRemoveWallNormalVelocity(
                new Vector3(2f, 0f, 0f), outward, 1, out corrected));
            AssertVector(new Vector3(2f, 0f, 0f), corrected);

            // Parallel motion is tangential: untouched by the same normal.
            Assert.IsTrue(SweepMath.TryRemoveWallNormalVelocity(
                new Vector3(0f, 0f, 3f), outward, 1, out corrected));
            AssertVector(new Vector3(0f, 0f, 3f), corrected);
        }

        [Test]
        public void CornerSweepRemovesBothNormalComponents()
        {
            // Two half-spaces x >= 0 and z >= 0 are hit at the same time; the two
            // inward components are removed in contact order, leaving nothing.
            Vector3[] normals = { new Vector3(1f, 0f, 0f), new Vector3(0f, 0f, 1f) };
            float[] offsets = { 0f, 0f };
            SweepResult sweep = Sweep(new Vector3(2f, 0f, 2f), new Vector3(-4f, 0f, -4f),
                0.5f, normals, offsets);
            Assert.AreEqual(2, sweep.Contacts.Length);

            Vector3[] wallNormals = new Vector3[4];
            int wallNormalCount;
            Assert.IsTrue(CollectWallNormals(sweep, normals, offsets, null, null,
                wallNormals, out wallNormalCount));
            Assert.AreEqual(2, wallNormalCount);
            // Both normals point out of their obstacle (the set is +X and +Z).
            Assert.AreEqual(1f, wallNormals[0].x + wallNormals[1].x, 0f);
            Assert.AreEqual(1f, wallNormals[0].z + wallNormals[1].z, 0f);

            Vector3 corrected;
            Assert.IsTrue(SweepMath.TryRemoveWallNormalVelocity(
                new Vector3(-3f, 0f, -3f), wallNormals, wallNormalCount, out corrected));
            AssertVector(Vector3.zero, corrected);

            // Moving out of one wall while sliding into the other removes only
            // the component that points in.
            Assert.IsTrue(SweepMath.TryRemoveWallNormalVelocity(
                new Vector3(-3f, 0f, 4f), wallNormals, wallNormalCount, out corrected));
            Assert.AreEqual(0f, corrected.x, Tolerance);
            Assert.AreEqual(4f, corrected.z, Tolerance);
        }

        [Test]
        public void NonFiniteOrDegenerateNormalsAreRefused()
        {
            Vector3 velocity = new Vector3(-1f, 0f, 0f);
            Vector3 corrected;
            Assert.IsFalse(SweepMath.TryRemoveWallNormalVelocity(
                velocity, new[] { Vector3.zero }, 1, out corrected));
            AssertVector(Vector3.zero, corrected);
            Assert.IsFalse(SweepMath.TryRemoveWallNormalVelocity(
                velocity, new[] { new Vector3(float.NaN, 0f, 1f) }, 1, out corrected));
            Assert.IsFalse(SweepMath.TryRemoveWallNormalVelocity(
                velocity, new[] { new Vector3(float.PositiveInfinity, 0f, 0f) }, 1, out corrected));
            Assert.IsFalse(SweepMath.TryRemoveWallNormalVelocity(
                new Vector3(float.NaN, 0f, 0f), new[] { Vector3.right }, 1, out corrected));
            Assert.IsFalse(SweepMath.TryRemoveWallNormalVelocity(
                velocity, null, 0, out corrected));
            Assert.IsFalse(SweepMath.TryRemoveWallNormalVelocity(
                velocity, new[] { Vector3.right }, -1, out corrected));
            Assert.IsFalse(SweepMath.TryRemoveWallNormalVelocity(
                velocity, new[] { Vector3.right }, 2, out corrected));

            // A valid but purely vertical normal is a floor/support contact: it is
            // ignored, not refused, because a planar velocity has no such part.
            Assert.IsTrue(SweepMath.TryRemoveWallNormalVelocity(
                velocity, new[] { new Vector3(0f, 1f, 0f) }, 1, out corrected));
            AssertVector(velocity, corrected);

            // The collector refuses corrupt or mismatched contact data instead of
            // writing a normal derived from it.
            int wallNormalCount;
            Vector3[] wallNormals = new Vector3[1];
            Assert.IsFalse(SweepMath.TryCollectWallContactNormals(
                new[] { Vector3.zero }, new[] { Vector3.forward }, new[] { 0 },
                1, new[] { Vector3.zero }, new[] { 0f }, new Vector3[0], new Vector3[0],
                wallNormals, out wallNormalCount));
            Assert.AreEqual(0, wallNormalCount);
            Assert.IsFalse(SweepMath.TryCollectWallContactNormals(
                new[] { Vector3.zero }, new[] { Vector3.forward }, new[] { 1 },
                1, new[] { Vector3.right }, new[] { 0f }, new Vector3[0], new Vector3[0],
                wallNormals, out wallNormalCount));
            Assert.IsFalse(SweepMath.TryCollectWallContactNormals(
                new[] { Vector3.zero }, new[] { Vector3.forward }, new[] { 0 },
                2, new[] { Vector3.right }, new[] { 0f }, new Vector3[0], new Vector3[0],
                new Vector3[2], out wallNormalCount));
            Assert.AreEqual(0, wallNormalCount);
        }

        [Test]
        public void BoxEnteredAlongItsNegativeFaceIsOrientedOutward()
        {
            // Moving in -X into box [-2,-1] reports the inner face normal -X,
            // because the sweep keeps the reference's slab sign rule for value
            // parity. The collector must re-orient it to +X, otherwise the
            // velocity rule would ignore the hit and the ball would keep its
            // full speed into the wall (the reported bug).
            Vector3[] minima = { new Vector3(-2f, -1f, -1f) };
            Vector3[] maxima = { new Vector3(-1f, 1f, 1f) };
            SweepResult sweep = Sweep(new Vector3(0f, 0f, 0f), new Vector3(-2f, 0f, 0f),
                0.5f, null, null, minima, maxima);
            Assert.AreEqual(-0.5f, sweep.Position.x, Tolerance);
            Assert.GreaterOrEqual(sweep.Contacts.Length, 1);
            Assert.AreEqual(-1f, sweep.Contacts[0].Normal.x, 0f);

            Vector3[] wallNormals = new Vector3[4];
            int wallNormalCount;
            Assert.IsTrue(CollectWallNormals(sweep, null, null, minima, maxima,
                wallNormals, out wallNormalCount));
            Assert.AreEqual(sweep.Contacts.Length, wallNormalCount);
            for (int i = 0; i < wallNormalCount; i++)
            {
                Assert.AreEqual(1f, wallNormals[i].x, 0f);
                Assert.AreEqual(0f, wallNormals[i].y, 0f);
                Assert.AreEqual(0f, wallNormals[i].z, 0f);
            }

            Vector3 corrected;
            Assert.IsTrue(SweepMath.TryRemoveWallNormalVelocity(
                new Vector3(-3.95f, 0f, 0f), wallNormals, wallNormalCount, out corrected));
            AssertVector(Vector3.zero, corrected);
        }

        // Runs one TrySweepSphere call with test-owned buffers and copies the
        // recorded contacts out so each case reads like the Python result object.
        private static SweepResult Sweep(
            Vector3 start,
            Vector3 displacement,
            float radius,
            Vector3[] planeNormals = null,
            float[] planeOffsets = null,
            Vector3[] boxMinima = null,
            Vector3[] boxMaxima = null,
            int maxContacts = 8)
        {
            if (planeNormals == null)
            {
                planeNormals = new Vector3[0];
                planeOffsets = new float[0];
            }
            if (boxMinima == null)
            {
                boxMinima = new Vector3[0];
                boxMaxima = new Vector3[0];
            }

            Vector3[] centers = new Vector3[maxContacts];
            Vector3[] points = new Vector3[maxContacts];
            Vector3[] normals = new Vector3[maxContacts];
            float[] times = new float[maxContacts];
            int[] indices = new int[maxContacts];
            float[] depths = new float[maxContacts];

            SweepResult result = new SweepResult();
            int count;
            result.Accepted = SweepMath.TrySweepSphere(
                start, displacement, radius,
                planeNormals, planeOffsets, boxMinima, boxMaxima,
                maxContacts, SeparationEpsilon,
                centers, points, normals, times, indices, depths,
                out count,
                out result.Position, out result.Remaining, out result.Slide,
                out result.Stuck, out result.Blocked);

            result.Contacts = new ContactRecord[count];
            result.ContactCenters = new Vector3[count];
            result.ContactNormals = new Vector3[count];
            result.ContactObstacleIndices = new int[count];
            for (int i = 0; i < count; i++)
            {
                ContactRecord contact = new ContactRecord();
                contact.Time = times[i];
                contact.Center = centers[i];
                contact.Point = points[i];
                contact.Normal = normals[i];
                contact.ObstacleIndex = indices[i];
                contact.PenetrationDepth = depths[i];
                result.Contacts[i] = contact;
                result.ContactCenters[i] = centers[i];
                result.ContactNormals[i] = normals[i];
                result.ContactObstacleIndices[i] = indices[i];
            }
            return result;
        }

        // Turns a sweep result's recorded contacts into outward wall normals the
        // way the motor does, with test-owned buffers and the null-obstacle
        // convention of Sweep's optional arguments.
        private static bool CollectWallNormals(
            SweepResult result,
            Vector3[] planeNormals,
            float[] planeOffsets,
            Vector3[] boxMinima,
            Vector3[] boxMaxima,
            Vector3[] wallNormals,
            out int wallNormalCount)
        {
            if (planeNormals == null)
            {
                planeNormals = new Vector3[0];
                planeOffsets = new float[0];
            }
            if (boxMinima == null)
            {
                boxMinima = new Vector3[0];
                boxMaxima = new Vector3[0];
            }
            return SweepMath.TryCollectWallContactNormals(
                result.ContactCenters, result.ContactNormals, result.ContactObstacleIndices,
                result.Contacts.Length, planeNormals, planeOffsets, boxMinima, boxMaxima,
                wallNormals, out wallNormalCount);
        }

        private static void AssertSameResult(SweepResult expected, SweepResult actual)
        {
            Assert.AreEqual(expected.Accepted, actual.Accepted);
            AssertVectorExact(expected.Position, actual.Position);
            AssertVectorExact(expected.Remaining, actual.Remaining);
            AssertVectorExact(expected.Slide, actual.Slide);
            Assert.AreEqual(expected.Stuck, actual.Stuck);
            Assert.AreEqual(expected.Blocked, actual.Blocked);
            Assert.AreEqual(expected.Contacts.Length, actual.Contacts.Length);
            for (int i = 0; i < expected.Contacts.Length; i++)
            {
                Assert.AreEqual(expected.Contacts[i].Time, actual.Contacts[i].Time, 0f);
                AssertVectorExact(expected.Contacts[i].Center, actual.Contacts[i].Center);
                AssertVectorExact(expected.Contacts[i].Point, actual.Contacts[i].Point);
                AssertVectorExact(expected.Contacts[i].Normal, actual.Contacts[i].Normal);
                Assert.AreEqual(expected.Contacts[i].ObstacleIndex, actual.Contacts[i].ObstacleIndex);
                Assert.AreEqual(expected.Contacts[i].PenetrationDepth,
                    actual.Contacts[i].PenetrationDepth, 0f);
            }
        }

        private static void AssertVector(Vector3 expected, Vector3 actual)
        {
            Assert.AreEqual(expected.x, actual.x, Tolerance, "vector x");
            Assert.AreEqual(expected.y, actual.y, Tolerance, "vector y");
            Assert.AreEqual(expected.z, actual.z, Tolerance, "vector z");
        }

        // Determinism means bit-identical, so this comparison allows no delta.
        private static void AssertVectorExact(Vector3 expected, Vector3 actual)
        {
            Assert.AreEqual(expected.x, actual.x, 0f, "vector x");
            Assert.AreEqual(expected.y, actual.y, 0f, "vector y");
            Assert.AreEqual(expected.z, actual.z, 0f, "vector z");
        }
    }
}
